using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): tests focales de
/// <see cref="ExchangeRateSyncJob"/> — idempotencia, degradacion sin excepcion, respaldo BNA, y que
/// el backfill SOLO llena huecos. <see cref="IAfipService"/> y <see cref="IBnaExchangeRateService"/>
/// van mockeados (Moq): el job en si no le pega a ARCA ni scrapea nada, solo orquesta.
/// </summary>
public class ExchangeRateSyncJobTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly DateOnly Today = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());

    private static async Task SeedAfipSettingsAsync(AppDbContext ctx, bool isProduction = false)
    {
        ctx.AfipSettings.Add(new AfipSettings { Id = 1, IsProduction = isProduction });
        await ctx.SaveChangesAsync();
    }

    // ============================================================
    // Test 9 (spec §15): idempotencia — dos corridas seguidas no duplican filas ni alteran Rate.
    // ============================================================

    [Fact]
    public async Task DosCorridasSeguidas_NoDuplicanFilas_NiAlteranElRate()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1350.50m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);

        await job.RunAsync(CancellationToken.None);
        var countAfterFirstRun = await ctx.ExchangeRateQuotes.CountAsync();
        Assert.True(countAfterFirstRun > 0);

        await job.RunAsync(CancellationToken.None);
        var countAfterSecondRun = await ctx.ExchangeRateQuotes.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
        Assert.All(await ctx.ExchangeRateQuotes.ToListAsync(), q => Assert.Equal(1350.50m, q.Rate));
    }

    // ============================================================
    // Test 10 (spec §15): ARCA devuelve null (Errors no vacio, ya filtrado por AfipService) ->
    // degrada sin excepcion y el job termina OK.
    // ============================================================

    [Fact]
    public async Task ArcaDevuelveNullParaTodo_ElJobTerminaSinTirar_YSinFilas()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();
        bnaMock
            .Setup(s => s.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);

        var exception = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(0, await ctx.ExchangeRateQuotes.CountAsync());
    }

    // ============================================================
    // Test 11 (spec §15): ARCA caido para HOY -> intenta el respaldo BNA, persiste con
    // Source=BNA_Minorista.
    // ============================================================

    [Fact]
    public async Task ArcaCaidoParaHoy_PersisteElRespaldoBna_ConSourceBnaMinorista()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);

        var bnaMock = new Mock<IBnaExchangeRateService>();
        bnaMock
            .Setup(s => s.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BnaUsdSellerRateDto(
                Value: 1349m, EuroValue: 0m, RealValue: 0m,
                PublishedDate: Today.ToString("d/M/yyyy"), PublishedTime: "15:00",
                Source: "https://www.bna.com.ar/personas", IsStale: false, FetchedAt: DateTime.UtcNow));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);

        Assert.NotNull(filaDeHoy);
        Assert.Equal(ExchangeRateSource.BNA_Minorista, filaDeHoy!.Source);
        Assert.Equal("BNA_Scraper", filaDeHoy.ProviderName);
        Assert.Equal(1349m, filaDeHoy.Rate);
    }

    /// <summary>
    /// Complemento del test 11: un snapshot BNA STALE (resucitado del fallback interno del scraper,
    /// no recien fetcheado) NO se persiste como "cotizacion de hoy" — no es honesto etiquetarlo asi.
    /// </summary>
    [Fact]
    public async Task ArcaCaidoParaHoy_ConRespaldoBnaStale_NoPersisteNada()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);

        var bnaMock = new Mock<IBnaExchangeRateService>();
        bnaMock
            .Setup(s => s.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BnaUsdSellerRateDto(
                Value: 1349m, EuroValue: 0m, RealValue: 0m,
                PublishedDate: Today.AddDays(-3).ToString("d/M/yyyy"), PublishedTime: "15:00",
                Source: "https://www.bna.com.ar/personas", IsStale: true, FetchedAt: DateTime.UtcNow));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);
        Assert.Null(filaDeHoy);
    }

    // ============================================================
    // Test 12 (spec §15): backfill SOLO llena huecos; no pisa filas existentes.
    // ============================================================

    [Fact]
    public async Task Backfill_NoPisaFilasExistentes_SoloLlenaLosHuecos()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        // Ya hay una fila (valor "viejo") para hace 2 dias — simula que un job anterior ya la cubrio.
        var fechaYaCubierta = Today.AddDays(-2);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fechaYaCubierta,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1300m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow.AddDays(-1),
            ArcaFchCotiz = fechaYaCubierta,
            IsProductionSource = false,
        });
        await ctx.SaveChangesAsync();

        // El mock devolveria un valor DISTINTO para CUALQUIER fecha si lo llamaran — el punto del
        // test es probar que NO lo llama para la fecha ya cubierta.
        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 9999m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        var filaVieja = await ctx.ExchangeRateQuotes
            .SingleAsync(q => q.Currency == "USD" && q.QuoteDate == fechaYaCubierta);
        Assert.Equal(1300m, filaVieja.Rate); // sin pisar.

        afipMock.Verify(
            s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), fechaYaCubierta, It.IsAny<CancellationToken>()),
            Times.Never);

        // Otro dia del backfill que SI estaba vacio, se lleno con el valor del mock.
        var otroDiaDelBackfill = Today.AddDays(-1);
        var filaNueva = await ctx.ExchangeRateQuotes
            .SingleAsync(q => q.Currency == "USD" && q.QuoteDate == otroDiaDelBackfill);
        Assert.Equal(9999m, filaNueva.Rate);
    }

    // ============================================================
    // Test 13 (spec §15, defensa en profundidad a nivel job): un Rate invalido que llegara desde
    // IAfipService (aunque AfipService real ya lo filtra en el parseo) NO se persiste.
    // ============================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public async Task ConRateInvalidoDesdeAfipService_NoSePersisteNada(int rateInvalido)
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, rateInvalido, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, await ctx.ExchangeRateQuotes.CountAsync());
    }

    // ============================================================
    // Cobertura extra: la corrida respeta el entorno (IsProductionSource) segun AfipSettings.
    // ============================================================

    [Fact]
    public async Task LasFilasQuedanMarcadas_ConElEntornoVigenteDeAfipSettings()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: true);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1350.50m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        Assert.All(await ctx.ExchangeRateQuotes.ToListAsync(), q => Assert.True(q.IsProductionSource));
    }
}
