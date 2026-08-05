using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real") — fix del detalle #3 de la revision
/// post-implementacion: <c>ExchangeRateSyncJob.EnsureRowExistsAsync</c> atrapaba el
/// <c>DbUpdateException</c> de una violacion UNIQUE real (dos corridas del job pisandose), pero
/// dejaba la entidad recien creada en estado <c>Added</c> DENTRO del <c>ChangeTracker</c>. El
/// PROXIMO <c>SaveChangesAsync</c> de esa misma corrida (backfill de otra fecha) volvia a incluir
/// esa entidad rota en el batch y hacia rebotar TODA la corrida restante con el mismo error — el
/// backfill se cortaba en la primera fecha que chocara, en vez de seguir con las demas.
///
/// <para>Necesita Postgres REAL (Testcontainers): InMemory no tiene indices UNIQUE ni tira
/// <c>PostgresException</c>, asi que el bug era invisible en la suite unit.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExchangeRateSyncJobPostgresIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ExchangeRateSyncJobPostgresIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunAsync_ConUnaFechaQueChocaPorUniqueViolation_SigueYEscribeLasDemasFechas()
    {
        await using var ctx = _fixture.CreateDbContext();
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false });
        await ctx.SaveChangesAsync();

        var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());

        // Pre-cargamos (tracked, Added, SIN guardar todavia) una fila con la MISMA clave natural
        // que el job va a intentar insertar para HOY (Currency/QuoteDate/Source/IsProductionSource).
        // Cuando el job llame a SU PRIMER SaveChangesAsync (al procesar "hoy"), EF incluye en el
        // MISMO batch esta fila pre-cargada + la que arma el job -> el indice UNIQUE de Postgres
        // rechaza el batch completo con una violacion REAL (no simulada) -> exactamente el
        // escenario que la revision pidio blindar.
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = hoyArgentina,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1m, // valor irrelevante: esta fila nunca se termina persistiendo (el batch entero rebota).
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = hoyArgentina,
            IsProductionSource = false,
        });

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1350.50m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);

        // No debe tirar: el catch atrapa la violacion UNIQUE de "hoy" y la corrida sigue.
        var exception = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));
        Assert.Null(exception);

        // La fecha del backfill INMEDIATAMENTE siguiente ("ayer") SI se escribio: si el fix no
        // funcionara, la entidad rota de "hoy" seguiria en el tracker y este SaveChanges tambien
        // rebotaria, dejando la tabla vacia.
        var ayer = hoyArgentina.AddDays(-1);
        var filaDeAyer = await ctx.ExchangeRateQuotes
            .AsNoTracking()
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == ayer && q.IsProductionSource == false);

        Assert.NotNull(filaDeAyer);
        Assert.Equal(1350.50m, filaDeAyer!.Rate);
    }
}
