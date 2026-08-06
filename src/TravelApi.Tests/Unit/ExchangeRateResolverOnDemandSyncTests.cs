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

    // ─── TRABAJO 2 (boton "actualizar" de la tira del dolar, 2026-08-05) ──────────────────────────

    [Fact]
    public async Task RequestManualSyncAsync_ConClienteDeHangfireInyectado_EncolaYDevuelveTrue()
    {
        await using var ctx = NewContext();

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var resolver = new ExchangeRateResolver(
            ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        var encolo = await resolver.RequestManualSyncAsync("USD", CancellationToken.None);

        Assert.True(encolo);
        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestManualSyncAsync_SinClienteDeHangfireInyectado_NoTiraExcepcion_YDevuelveFalse()
    {
        await using var ctx = NewContext();

        // Ctor "corto" (sin IBackgroundJobClient), mismo criterio que el resto de esta clase.
        var resolver = new ExchangeRateResolver(ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance);

        var encolo = await resolver.RequestManualSyncAsync("USD", CancellationToken.None);

        Assert.False(encolo);
    }

    [Fact]
    public async Task RequestManualSyncAsync_LlamadoDosVecesSeguidas_SoloEncolaLaPrimera_PorElDebounce()
    {
        await using var ctx = NewContext();

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var sharedCache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ExchangeRateResolver(
            ctx, sharedCache, NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        var primerClick = await resolver.RequestManualSyncAsync("USD", CancellationToken.None);
        var segundoClick = await resolver.RequestManualSyncAsync("USD", CancellationToken.None);

        Assert.True(primerClick);
        Assert.False(segundoClick);
        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    /// <summary>
    /// TRABAJO 2 exige, con todas las letras, que el boton "reuse el MISMO debounce de 5' del
    /// resolver": el disparo AUTOMATICO de <see cref="ExchangeRateResolver.GetSuggestionAsync"/> y el
    /// disparo MANUAL de <see cref="ExchangeRateResolver.RequestManualSyncAsync"/> comparten la MISMA
    /// clave de cache — si uno ya abrio la ventana de 5 minutos, el otro no puede encolar de nuevo
    /// hasta que pase el TTL.
    /// </summary>
    [Fact]
    public async Task RequestManualSyncAsync_CompartenElDebounceConElDisparoAutomatico_DeGetSuggestionAsync()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var jobClientMock = new Mock<IBackgroundJobClient>();
        var sharedCache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ExchangeRateResolver(
            ctx, sharedCache, NullLogger<ExchangeRateResolver>.Instance, jobClientMock.Object);

        // El disparo AUTOMATICO (una consulta normal, sin fila de hoy) ya abre la ventana de debounce.
        await resolver.GetSuggestionAsync("USD", Today, CancellationToken.None);

        // El usuario aprieta el boton "actualizar" justo despues: como comparten la misma clave, NO
        // deberia encolar una segunda vez.
        var encoloElBoton = await resolver.RequestManualSyncAsync("USD", CancellationToken.None);

        Assert.False(encoloElBoton);
        jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }
}
