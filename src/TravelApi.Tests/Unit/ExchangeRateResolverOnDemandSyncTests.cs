using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): tests focales del disparo ON-DEMAND de
/// <see cref="ExchangeRateResolver"/> — si nadie pidio TODAVIA la cotizacion de HOY para una moneda,
/// el resolver encola <see cref="ExchangeRateSyncJob"/> (fire-and-forget, via
/// <see cref="IBackgroundJobClient"/>) sin esperarlo, con un debounce de 5 minutos para no encolar
/// de mas. Son tests UNITARIOS (InMemory), no tocan Postgres ni Hangfire real.
/// </summary>
public class ExchangeRateResolverOnDemandSyncTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task SeedAfipSettingsAsync(AppDbContext ctx, bool isProduction = false)
    {
        ctx.AfipSettings.Add(new AfipSettings { Id = 1, IsProduction = isProduction });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task SinFilaDeHoy_ConClienteDeHangfireInyectado_EncolaLaSincronizacion()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var resolver = new ExchangeRateResolver(
            ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        await resolver.GetSuggestionAsync("USD", Today, CancellationToken.None);

        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task ConFilaDeHoyYaExistente_NoEncolaNada()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = Today,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1520m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = Today,
            IsProductionSource = false,
        });
        await ctx.SaveChangesAsync();

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var resolver = new ExchangeRateResolver(
            ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        await resolver.GetSuggestionAsync("USD", Today, CancellationToken.None);

        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Never);
    }

    [Fact]
    public async Task DosLlamadasSeguidas_SinFilaDeHoy_SoloEncolaUnaVez_PorElDebounce()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var jobClientMock = new Mock<IBackgroundJobClient>();
        // MISMA instancia de IMemoryCache para las dos llamadas: el debounce vive ahi.
        var sharedCache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ExchangeRateResolver(
            ctx, sharedCache, NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        await resolver.GetSuggestionAsync("USD", Today, CancellationToken.None);
        await resolver.GetSuggestionAsync("USD", Today, CancellationToken.None);

        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task SinClienteDeHangfireInyectado_NoTiraExcepcion_YSeComportaComoAntesDeEstaObra()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        // Ctor "corto" (mismo criterio que ExchangeRateSyncJob sin IOfficialDollarPublicApiService):
        // sin backgroundJobClient inyectado, el resolver no debe intentar encolar nada ni tirar.
        var resolver = new ExchangeRateResolver(ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance);

        var exception = await Record.ExceptionAsync(
            () => resolver.GetSuggestionAsync("USD", Today, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ParaPesos_NoConsultaNiEncolaNada()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var resolver = new ExchangeRateResolver(
            ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        var suggestion = await resolver.GetSuggestionAsync("ARS", Today, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(1m, suggestion!.Rate);
        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Never);
    }
}
