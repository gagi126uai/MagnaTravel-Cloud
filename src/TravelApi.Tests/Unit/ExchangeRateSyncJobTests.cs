using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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

    /// <summary>Logger que captura los entries en memoria — usado para verificar el Warning de coherencia.</summary>
    private sealed class CapturingLogger : ILogger<ExchangeRateSyncJob>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
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

    // ============================================================
    // ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo"): respaldo REAL via API publica.
    // Orden para HOY: ARCA -> API publica -> scraper BNA. Orden para backfill: ARCA -> API publica
    // (el scraper NO participa del backfill, solo sabe dar el dato de "ahora").
    // ============================================================

    /// <summary>
    /// Test: ARCA cae para HOY, la API publica SI contesta -> persiste con Source=OficialPorApi,
    /// IsProductionSource=true SIEMPRE (es un dato real, no depende del entorno de ARCA), y el scraper
    /// BNA ni se llama (ya quedo cubierto).
    /// </summary>
    [Fact]
    public async Task ArcaCaidoParaHoy_ConApiPublicaDisponible_PersisteOficialPorApi_YNoLlamaAlScraperBna()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock
            .Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1496.50m, ProviderName: "dolarapi"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);

        Assert.NotNull(filaDeHoy);
        Assert.Equal(ExchangeRateSource.OficialPorApi, filaDeHoy!.Source);
        Assert.Equal("dolarapi", filaDeHoy.ProviderName);
        Assert.Equal(1496.50m, filaDeHoy.Rate);
        Assert.True(filaDeHoy.IsProductionSource);

        bnaMock.Verify(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Test: ARCA cae para HOY, la API publica TAMBIEN falla -> el job cae al scraper BNA como
    /// siempre (comportamiento preexistente, sin cambios).
    /// </summary>
    [Fact]
    public async Task ArcaCaidoParaHoy_ConApiPublicaSinDato_CaeAlScraperBna()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock
            .Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicDollarRateReading?)null);

        var bnaMock = new Mock<IBnaExchangeRateService>();
        bnaMock
            .Setup(s => s.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BnaUsdSellerRateDto(
                Value: 1349m, EuroValue: 0m, RealValue: 0m,
                PublishedDate: Today.ToString("d/M/yyyy"), PublishedTime: "15:00",
                Source: "https://www.bna.com.ar/personas", IsStale: false, FetchedAt: DateTime.UtcNow));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);

        Assert.NotNull(filaDeHoy);
        Assert.Equal(ExchangeRateSource.BNA_Minorista, filaDeHoy!.Source);
    }

    /// <summary>
    /// Test: sin <see cref="IOfficialDollarPublicApiService"/> inyectado (ctor corto, como TODOS los
    /// tests de arriba de este archivo) el job se comporta EXACTO que antes de esta obra — cae directo
    /// al scraper BNA sin intentar nada nuevo.
    /// </summary>
    [Fact]
    public async Task SinServicioDeApiPublicaInyectado_SeComportaIgualQueAntes_CaeDirectoAlScraperBna()
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
    }

    /// <summary>
    /// Test: en el BACKFILL, ARCA cae para un dia -> la API publica (variante "por fecha") lo cubre.
    /// El scraper BNA NUNCA se llama para backfill (no tiene historial, solo el dato de "ahora").
    /// </summary>
    [Fact]
    public async Task Backfill_ConArcaCaidoParaUnDia_LoCubreConLaApiPublicaPorFecha()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var diaSinArca = Today.AddDays(-2);

        var afipMock = new Mock<IAfipService>();
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), diaSinArca, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        // El resto de fechas (hoy + otros dias del backfill) SI las cubre ARCA normalmente.
        afipMock
            .Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.Is<DateOnly>(d => d != diaSinArca), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1350.50m, fecha));

        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock
            .Setup(s => s.GetRateForDateAsync(diaSinArca, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1310m, ProviderName: "argentinadatos"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDelDiaSinArca = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == diaSinArca);

        Assert.NotNull(filaDelDiaSinArca);
        Assert.Equal(ExchangeRateSource.OficialPorApi, filaDelDiaSinArca!.Source);
        Assert.Equal("argentinadatos", filaDelDiaSinArca.ProviderName);
        Assert.Equal(1310m, filaDelDiaSinArca.Rate);

        // El scraper BNA nunca participa del backfill (solo sabe dar el dato de "ahora").
        bnaMock.Verify(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): escalera de CINCO APIs publicas para
    // HOY (dolarapi -> monedapi -> criptoya -> argentinadatos -> bluelytics). Cada test caido la
    // corta en un nivel distinto para probar que el siguiente SI se intenta y los de mas atras NO.
    // ============================================================

    [Fact]
    public async Task Escalera_DolarApiYMonedApiCaen_CriptoYaContesta_PersisteConProviderCriptoya()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromMonedApiAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromCriptoYaAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1520m, ProviderName: "criptoya"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);
        Assert.NotNull(filaDeHoy);
        Assert.Equal("criptoya", filaDeHoy!.ProviderName);
        Assert.Equal(1520m, filaDeHoy.Rate);

        // argentinadatos (variante de HOY) y bluelytics estan MAS ABAJO en la escalera de HOY: no
        // deberian llamarse para la fecha de hoy porque criptoya ya la cubrio. El mock SI recibe
        // llamados a GetRateForDateAsync para las fechas del BACKFILL (ARCA tambien falla ahi, y ese
        // camino es independiente de la escalera de hoy) — por eso la verificacion se acota a "Today".
        publicApiMock.Verify(s => s.GetRateForDateAsync(Today, It.IsAny<CancellationToken>()), Times.Never);
        publicApiMock.Verify(s => s.GetTodayRateFromBluelyticsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Escalera_PrimerasTresApisCaen_ArgentinaDatosContestaParaHoy_PersisteConEseProvider()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromMonedApiAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromCriptoYaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        // Variante "por fecha" de argentinadatos.com sirve TAMBIEN el dia de hoy (verificado con curl).
        publicApiMock.Setup(s => s.GetRateForDateAsync(Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1520m, ProviderName: "argentinadatos"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);
        Assert.NotNull(filaDeHoy);
        Assert.Equal("argentinadatos", filaDeHoy!.ProviderName);

        publicApiMock.Verify(s => s.GetTodayRateFromBluelyticsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Bluelytics es el PROMEDIO de mercado, va al final de la escalera de APIs (antes de caer al
    /// scraper BNA): solo se usa si las otras cuatro fallaron las cuatro.
    /// </summary>
    [Fact]
    public async Task Escalera_LasCuatroApisAnterioresCaen_BluelyticsContesta_PersisteConEseProvider_YNoLlamaAlScraperBna()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromMonedApiAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromCriptoYaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetRateForDateAsync(Today, It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateFromBluelyticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1520m, ProviderName: "bluelytics"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);
        Assert.NotNull(filaDeHoy);
        Assert.Equal("bluelytics", filaDeHoy!.ProviderName);
        Assert.Equal(ExchangeRateSource.OficialPorApi, filaDeHoy.Source);

        bnaMock.Verify(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): guard barato de cadencia. Con el
    // recurring corriendo cada hora, la corrida debe cortar SIN llamar a nadie cuando el dia ya
    // esta resuelto — la definicion exacta de "resuelto" depende del entorno (ver
    // ExchangeRateSyncJob.IsTodayAlreadyFullyCoveredAsync).
    // ============================================================

    [Fact]
    public async Task GuardDeCadencia_EnHomologacion_ConFilaOficialPorApiDeHoy_CortaSinLlamarANadie()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = Today,
            Source = ExchangeRateSource.OficialPorApi,
            Rate = 1520m,
            ProviderName = "dolarapi",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = true,
        });
        await ctx.SaveChangesAsync();

        var afipMock = new Mock<IAfipService>();
        var bnaMock = new Mock<IBnaExchangeRateService>();
        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        // En homologacion, la fila OficialPorApi de hoy alcanza para el guard: NADA se llama, ni
        // siquiera ARCA para el backfill de los ultimos 7 dias.
        afipMock.Verify(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        bnaMock.Verify(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
        publicApiMock.Verify(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuardDeCadencia_EnProduccion_ConSoloFilaOficialPorApi_SinAfipOficialDeHoy_NoCorta()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: true);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = Today,
            Source = ExchangeRateSource.OficialPorApi,
            Rate = 1520m,
            ProviderName = "dolarapi",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = true,
        });
        await ctx.SaveChangesAsync();

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1520m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        // En produccion, con AfipOficial de hoy TODAVIA sin fila, el guard NO corta: ARCA se sigue
        // consultando (aca para hoy y para el backfill de 7 dias).
        afipMock.Verify(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), Today, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GuardDeCadencia_EnProduccion_ConOficialPorApiYAfipOficialDeHoy_CortaSinLlamarANadie()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: true);
        ctx.ExchangeRateQuotes.AddRange(
            new ExchangeRateQuote
            {
                Currency = "USD",
                QuoteDate = Today,
                Source = ExchangeRateSource.OficialPorApi,
                Rate = 1520m,
                ProviderName = "dolarapi",
                FetchedAt = DateTime.UtcNow,
                IsProductionSource = true,
            },
            new ExchangeRateQuote
            {
                Currency = "USD",
                QuoteDate = Today,
                Source = ExchangeRateSource.AfipOficial,
                Rate = 1520m,
                ProviderName = "ARCA_WSFEv1",
                FetchedAt = DateTime.UtcNow,
                ArcaFchCotiz = Today,
                IsProductionSource = true,
            });
        await ctx.SaveChangesAsync();

        var afipMock = new Mock<IAfipService>();
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        afipMock.Verify(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        bnaMock.Verify(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): defensa de coherencia. Dos fuentes del
    // mismo dia que difieren mas de 5% dejan un Warning en el log, pero NUNCA bloquean el guardado
    // (P-21/T-12: el sistema sugiere, no decide, y nunca se cae por un dato sospechoso).
    // ============================================================

    [Fact]
    public async Task Coherencia_NuevaFuenteDivergeMasDe5PorcientoDeOtraDelMismoDia_NoBloqueaElGuardado()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);

        // Fila previa de HOY con un valor MUY distinto (simula, por ejemplo, una fuente vieja
        // desactualizada) — el punto del test es que el job NO se frena por esto.
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = Today,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1000m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = Today,
            IsProductionSource = true, // entorno DISTINTO al de esta corrida (false) -> ARCA la vuelve a intentar.
        });
        await ctx.SaveChangesAsync();

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1520m, ProviderName: "dolarapi")); // 52% de diferencia con 1000.

        var capturingLogger = new CapturingLogger();
        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), capturingLogger,
            officialDollarPublicApiService: publicApiMock.Object);

        var exception = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));

        Assert.Null(exception);
        var filaNueva = await ctx.ExchangeRateQuotes
            .SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today && q.Source == ExchangeRateSource.OficialPorApi);
        Assert.NotNull(filaNueva);
        Assert.Equal(1520m, filaNueva!.Rate); // se guarda TAL CUAL, la divergencia no lo altera ni lo bloquea.

        Assert.Contains(capturingLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("diferencia"));
    }

    // ============================================================
    // Ampliacion 2026-08-06 ("el euro y el real tampoco tienen que faltar"): una sola corrida de
    // RunAsync sincroniza USD/EUR/BRL. EUR/BRL SIN ARCA (no la cotiza) y SIN scraper BNA (ese dato se
    // compara despues, en ReportService) — solo la escalera de APIs publicas.
    // ============================================================

    [Fact]
    public async Task RunAsync_SincronizaLasTresMonedas_ConUnaSolaCorrida()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1520m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateForEurAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1731.60m, ProviderName: "dolarapi"));
        publicApiMock.Setup(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 291.20m, ProviderName: "dolarapi"));
        // El backfill de EUR/BRL (7 dias) tambien pega contra estos metodos: se les da respuesta para
        // que el test no dependa de que fechas puntuales el mock deje sin configurar.
        publicApiMock.Setup(s => s.GetEurRateForDateAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1700m, ProviderName: "argentinadatos"));
        publicApiMock.Setup(s => s.GetBrlRateForDateAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 285m, ProviderName: "argentinadatos"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaUsdDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "USD" && q.QuoteDate == Today);
        var filaEurDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "EUR" && q.QuoteDate == Today);
        var filaBrlDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "BRL" && q.QuoteDate == Today);

        Assert.NotNull(filaUsdDeHoy);
        Assert.Equal(ExchangeRateSource.AfipOficial, filaUsdDeHoy!.Source);

        Assert.NotNull(filaEurDeHoy);
        Assert.Equal(ExchangeRateSource.OficialPorApi, filaEurDeHoy!.Source);
        Assert.Equal("dolarapi", filaEurDeHoy.ProviderName);
        Assert.Equal(1731.60m, filaEurDeHoy.Rate);
        Assert.True(filaEurDeHoy.IsProductionSource);

        Assert.NotNull(filaBrlDeHoy);
        Assert.Equal(ExchangeRateSource.OficialPorApi, filaBrlDeHoy!.Source);
        Assert.Equal("dolarapi", filaBrlDeHoy.ProviderName);
        Assert.Equal(291.20m, filaBrlDeHoy.Rate);

        // ARCA nunca se le pregunta por EUR/BRL: ese proveedor solo cotiza dolar (MonId="DOL").
        afipMock.Verify(s => s.GetOfficialExchangeRateAsync("DOL", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>Escalera de EUR: sin criptoya (no existe metodo para eso). dolarapi cae, monedapi
    /// contesta -> persiste con ese proveedor.</summary>
    [Fact]
    public async Task RunAsync_EscaleraDeEuro_DolarApiCae_MonedApiContesta_PersisteConEseProvider()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1520m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateForEurAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateForEurFromMonedApiAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1764.68m, ProviderName: "monedapi"));
        publicApiMock.Setup(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 291.20m, ProviderName: "dolarapi"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaEurDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "EUR" && q.QuoteDate == Today);
        Assert.NotNull(filaEurDeHoy);
        Assert.Equal("monedapi", filaEurDeHoy!.ProviderName);
        Assert.Equal(1764.68m, filaEurDeHoy.Rate);

        // criptoya NO tiene metodo equivalente para euro/real: no hay nada que verificar "Never" aca
        // (la interfaz simplemente no lo expone), el punto lo cubre la compilacion misma.
        publicApiMock.Verify(s => s.GetTodayRateForEurFromBluelyticsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Escalera de REAL: sin criptoya NI bluelytics. dolarapi y monedapi caen, argentinadatos
    /// (variante de hoy) contesta.</summary>
    [Fact]
    public async Task RunAsync_EscaleraDeReal_DolarApiYMonedApiCaen_ArgentinaDatosContesta()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string monId, DateOnly fecha, CancellationToken _) => new ArcaExchangeRate(monId, 1520m, fecha));
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateForEurAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1731.60m, ProviderName: "dolarapi"));
        publicApiMock.Setup(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetTodayRateForBrlFromMonedApiAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PublicDollarRateReading?)null);
        publicApiMock.Setup(s => s.GetBrlRateForDateAsync(Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 291.20m, ProviderName: "argentinadatos"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        var filaBrlDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "BRL" && q.QuoteDate == Today);
        Assert.NotNull(filaBrlDeHoy);
        Assert.Equal("argentinadatos", filaBrlDeHoy!.ProviderName);
        Assert.Equal(291.20m, filaBrlDeHoy.Rate);
    }

    /// <summary>
    /// Guard de cadencia extendido (§7.2, "corta por moneda"): EUR ya tiene fila OficialPorApi de hoy
    /// -> ni se llama a ningun proveedor de EUR. BRL sigue sin cubrir -> SI se llama a sus proveedores.
    /// Cada moneda corta de forma INDEPENDIENTE, no todo-o-nada.
    /// </summary>
    [Fact]
    public async Task GuardDeCadencia_CortaPorMoneda_EurYaCubiertoNoLlamaANadie_BrlSinCubrirSiLlama()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "EUR",
            QuoteDate = Today,
            Source = ExchangeRateSource.OficialPorApi,
            Rate = 1731.60m,
            ProviderName = "dolarapi",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = true,
        });
        // USD tambien cubierto, para que el test aisle el comportamiento de EUR/BRL sin ruido de ARCA.
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = Today,
            Source = ExchangeRateSource.OficialPorApi,
            Rate = 1520m,
            ProviderName = "dolarapi",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = true,
        });
        await ctx.SaveChangesAsync();

        var afipMock = new Mock<IAfipService>();
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 291.20m, ProviderName: "dolarapi"));

        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            officialDollarPublicApiService: publicApiMock.Object);
        await job.RunAsync(CancellationToken.None);

        publicApiMock.Verify(s => s.GetTodayRateForEurAsync(It.IsAny<CancellationToken>()), Times.Never);
        publicApiMock.Verify(s => s.GetTodayRateForEurFromMonedApiAsync(It.IsAny<CancellationToken>()), Times.Never);

        publicApiMock.Verify(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>()), Times.Once);
        var filaBrlDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "BRL" && q.QuoteDate == Today);
        Assert.NotNull(filaBrlDeHoy);
    }

    /// <summary>
    /// Subclase de test (revision post-review 2026-08-06): sobreescribe SOLO <c>RunUsdSyncAsync</c>
    /// (<c>internal virtual</c> a proposito, ver su doc de clase) para simular una excepcion CRUDA e
    /// IMPREVISTA que se escape de todos los try/catch internos del camino de USD — algo que ningun
    /// mock de <c>IAfipService</c>/<c>IBnaExchangeRateService</c> puede lograr por si solo, porque esos
    /// caminos YA atrapan sus propias fallas esperadas (ver el doc de clase de
    /// <see cref="ExchangeRateSyncJob.RunAsync"/>).
    /// </summary>
    private sealed class UsdSyncThrowsUnexpectedlyJob : ExchangeRateSyncJob
    {
        public UsdSyncThrowsUnexpectedlyJob(
            AppDbContext context, IAfipService afipService, IBnaExchangeRateService bnaExchangeRateService,
            IMemoryCache cache, ILogger<ExchangeRateSyncJob> logger, IOfficialDollarPublicApiService? officialDollarPublicApiService)
            : base(context, afipService, bnaExchangeRateService, cache, logger, officialDollarPublicApiService)
        {
        }

        internal override Task RunUsdSyncAsync(DateOnly today, CancellationToken ct) =>
            throw new InvalidOperationException("Fallo IMPREVISTO simulado en el camino de USD (ej. Postgres cayendo a mitad de una lectura).");
    }

    /// <summary>
    /// Hallazgo de review (2026-08-06): antes de este fix, una excepcion cruda en el camino de USD
    /// tumbaba TODA la corrida y EUR/BRL se quedaban sin sincronizar esa hora — el guard de cadencia
    /// por moneda no protege contra esto, una excepcion salta el guard directo. Ahora cada moneda va
    /// en su propio try/catch (<see cref="ExchangeRateSyncJob.RunCurrencySyncSafelyAsync"/>): USD
    /// revienta, pero EUR y BRL igual se sincronizan en la MISMA corrida.
    /// </summary>
    [Fact]
    public async Task RunAsync_SiUsdTiraExcepcionImprevista_EurYBrlIgualSeSincronizan()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        var bnaMock = new Mock<IBnaExchangeRateService>();

        var publicApiMock = new Mock<IOfficialDollarPublicApiService>();
        publicApiMock.Setup(s => s.GetTodayRateForEurAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 1731.60m, ProviderName: "dolarapi"));
        publicApiMock.Setup(s => s.GetTodayRateForBrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicDollarRateReading(Rate: 291.20m, ProviderName: "dolarapi"));

        var job = new UsdSyncThrowsUnexpectedlyJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance,
            publicApiMock.Object);

        var exception = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));

        Assert.Null(exception); // RunAsync en si nunca tira, aunque una moneda interna reviente.
        Assert.False(await ctx.ExchangeRateQuotes.AnyAsync(q => q.Currency == "USD" && q.QuoteDate == Today)); // USD reventó, sin fila.

        var filaEurDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "EUR" && q.QuoteDate == Today);
        var filaBrlDeHoy = await ctx.ExchangeRateQuotes.SingleOrDefaultAsync(q => q.Currency == "BRL" && q.QuoteDate == Today);
        Assert.NotNull(filaEurDeHoy);
        Assert.NotNull(filaBrlDeHoy);
    }

    [Fact]
    public async Task SinServicioDeApiPublicaInyectado_EurYBrl_NoIntentanNadaYNoDejanFilas()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx);

        var afipMock = new Mock<IAfipService>();
        afipMock.Setup(s => s.GetOfficialExchangeRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArcaExchangeRate?)null);
        var bnaMock = new Mock<IBnaExchangeRateService>();
        bnaMock.Setup(s => s.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);

        // Sin officialDollarPublicApiService inyectado (ctor de 5 args, igual que los tests viejos).
        var job = new ExchangeRateSyncJob(
            ctx, afipMock.Object, bnaMock.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateSyncJob>.Instance);
        var exception = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.False(await ctx.ExchangeRateQuotes.AnyAsync(q => q.Currency == "EUR"));
        Assert.False(await ctx.ExchangeRateQuotes.AnyAsync(q => q.Currency == "BRL"));
    }
}
