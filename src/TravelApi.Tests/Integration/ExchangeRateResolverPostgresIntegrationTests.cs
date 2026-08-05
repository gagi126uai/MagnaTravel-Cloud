using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real") — FIX BLOQUEANTE (revision post-implementacion):
/// la primera version de <see cref="ExchangeRateResolver.GetSuggestionAsync"/> ordenaba con
/// <c>.OrderBy(quote => SourcePrecedenceRank(quote.Source))</c>, una llamada a un metodo <c>static</c>
/// arbitrario DENTRO del <c>OrderBy</c>. Eso compila y los tests InMemory lo pasan (el proveedor
/// InMemory evalua el LINQ en memoria, no lo traduce a SQL), pero contra Postgres real
/// <c>InvalidOperationException: no se pudo traducir la expresion</c> en CADA resolucion — la
/// pantalla de facturar en USD hubiera quedado rota en produccion pese a que toda la suite unit daba
/// verde.
///
/// <para>Este test ejecuta la query REAL contra un Postgres real (Testcontainers, mismo patron que
/// <see cref="SearchServiceSqlTranslationIntegrationTests"/>). Es la unica red que hubiera atrapado
/// el bug antes de deployar.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExchangeRateResolverPostgresIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ExchangeRateResolverPostgresIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ExchangeRateResolver NewResolver(TravelApi.Infrastructure.Persistence.AppDbContext ctx) =>
        new(ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance);

    /// <summary>
    /// El caso que reventaba: varias filas de distinta fuente para la MISMA moneda+fecha+entorno.
    /// El ORDER BY de precedencia (§4.3) tiene que traducirse a SQL y devolver la AfipOficial, no
    /// tirar <c>InvalidOperationException</c>.
    /// </summary>
    [Fact]
    public async Task GetSuggestionAsync_ConVariasFuentesElMismoDia_TraduceElOrderByASql_YDevuelveLaAfipOficial()
    {
        await using var ctx = _fixture.CreateDbContext();

        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false });
        var fecha = new DateOnly(2026, 08, 05);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fecha,
            Source = ExchangeRateSource.BNA_Minorista,
            Rate = 1349m,
            ProviderName = "BNA_Scraper",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = false,
        });
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fecha,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1350.50m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = fecha,
            IsProductionSource = false,
        });
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);

        // Si el ORDER BY no fuera traducible, esto explota con InvalidOperationException aca mismo
        // (asi se rompio la primera version en Postgres real).
        var suggestion = await resolver.GetSuggestionAsync("USD", fecha, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(ExchangeRateSource.AfipOficial, suggestion!.Source);
        Assert.Equal(1350.50m, suggestion.Rate);
    }

    /// <summary>
    /// Mismo chequeo para el walk-back (rango de fechas + precedencia combinados): tambien tiene que
    /// traducir sin explotar, con varias filas de distintas fechas Y fuentes en el rango.
    /// </summary>
    [Fact]
    public async Task GetSuggestionAsync_ConWalkBackYVariasFuentes_TraduceASql_YRespetaPrecedenciaYFecha()
    {
        await using var ctx = _fixture.CreateDbContext();

        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false });
        var fechaPedida = new DateOnly(2026, 08, 05);

        // Hace 3 dias: BNA. Hace 2 dias: AfipOficial (mas reciente Y de mayor precedencia -> gana).
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fechaPedida.AddDays(-3),
            Source = ExchangeRateSource.BNA_Minorista,
            Rate = 1300m,
            ProviderName = "BNA_Scraper",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = false,
        });
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fechaPedida.AddDays(-2),
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1340m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = fechaPedida.AddDays(-2),
            IsProductionSource = false,
        });
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fechaPedida, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.IsStale);
        Assert.Equal(ExchangeRateSource.AfipOficial, suggestion.Source);
        Assert.Equal(fechaPedida.AddDays(-2), suggestion.RateDate);
        Assert.Equal(1340m, suggestion.Rate);
    }
}
