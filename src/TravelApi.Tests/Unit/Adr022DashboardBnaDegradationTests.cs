using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FIX 2 (2026-06-12): el dashboard NUNCA debe quedarse bloqueado esperando a Banco Nacion. La cotizacion del
/// dolar es informativa: si el fetch en vivo falla o tarda, el dashboard se degrada al ultimo snapshot
/// persistido y, si no hay, a null — pero siempre responde.
///
/// <para>Estos tests usan un mock de <see cref="IBnaExchangeRateService"/> que SIMULA la falla del fetch en
/// vivo (GetUsdSellerRateAsync lanza) y verifican que GetDashboardAsync igual responde, cayendo a
/// GetPersistedUsdSellerRateAsync (o a null). No verificamos el timeout real de 2s con un fetch HTTP de verdad
/// (eso seria un test lento/de integracion); cubrimos el contrato de degradacion, que es lo que evita el cuelgue.</para>
/// </summary>
public class Adr022DashboardBnaDegradationTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static BnaUsdSellerRateDto PersistedSnapshot() => new(
        Value: 1234.50m,
        EuroValue: 1300m,
        RealValue: 250m,
        PublishedDate: "10/06/2026",
        PublishedTime: "15:00",
        Source: "https://www.bna.com.ar/personas",
        IsStale: true,
        FetchedAt: DateTime.UtcNow.AddHours(-3));

    [Fact]
    public async Task Dashboard_WhenLiveBnaFetchThrows_DegradesToPersistedSnapshot()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        // El fetch en vivo "se cuelga"/falla: lanza. El dashboard NO debe propagar esto.
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("BNA no responde"));
        // El respaldo persistido (lectura local) si esta disponible.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot());

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Respondio (no se colgo ni tiro) y trae el snapshot degradado.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1234.50m, dashboard.BnaUsdSellerRate!.Value);
        Assert.True(dashboard.BnaUsdSellerRate.IsStale);
        bna.Verify(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dashboard_WhenLiveBnaFails_AndNoPersistedSnapshot_DegradesToNull()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // No hay snapshot persistido todavia.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // El dashboard responde igual; la cotizacion viene null (el front la tolera).
        Assert.Null(dashboard.BnaUsdSellerRate);
    }

    [Fact]
    public async Task Dashboard_WhenLiveBnaSucceeds_UsesLiveRate_AndDoesNotReadPersisted()
    {
        await using var context = CreateContext();

        var live = PersistedSnapshot() with { Value = 9999m, IsStale = false };

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(live);

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(9999m, dashboard.BnaUsdSellerRate!.Value);
        // Camino feliz: no se toca el respaldo persistido.
        bna.Verify(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): cuando el scraper del BNA se quedo sin dato
    /// (ni en vivo ni el ultimo snapshot persistido), el dashboard ya NO se queda mudo — cae a la libreta
    /// de <see cref="IExchangeRateResolver"/> (fuente ARCA) y lo etiqueta honestamente como "oficial".
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenBnaChainHasNoData_FallsBackToOfficialResolver_TaggedAsOficial()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Tampoco hay snapshot persistido: la cadena BNA de siempre queda en null.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var resolver = new Mock<IExchangeRateResolver>();
        // excludePracticeOfficialData EN true a proposito: esta tarjeta ("solo datos reales") tiene
        // que pedirle al resolver el modo honesto — si el service alguna vez regresara a pedir el modo
        // por defecto (false, el de facturar), este mock ya no matchea y el test cae a null.
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1152.202m,
                RateDate: new DateOnly(2026, 08, 04),
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: true,
                QuoteId: 7,
                FetchedAt: DateTime.UtcNow.AddHours(-1),
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1152.202m, dashboard.BnaUsdSellerRate!.Value);
        // La API publica de respaldo solo trae USD: nunca se inventa euro/real.
        Assert.Null(dashboard.BnaUsdSellerRate.EuroValue);
        Assert.Null(dashboard.BnaUsdSellerRate.RealValue);
        Assert.True(dashboard.BnaUsdSellerRate.IsStale);
    }

    /// <summary>
    /// Mismo escenario de arriba, pero la libreta oficial TAMPOCO tiene dato para hoy (job todavia no
    /// corrio, o esta fuera de la ventana de respaldo). El dashboard se comporta EXACTO que antes de esta
    /// obra: responde igual, la cotizacion viene null y el front la tolera.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenBnaChainHasNoData_AndOfficialResolverHasNoSuggestionEither_DegradesToNull()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.BnaUsdSellerRate);
    }

    /// <summary>
    /// TRABAJO 1 (bug real reportado en vivo por el dueño, 2026-08-05): "el dato mas nuevo gana".
    /// Antes, un snapshot persistido de HACE UN MES (scraper del BNA roto desde el 8/7) ganaba
    /// SIEMPRE con tal de no ser null — la fila fresca de HOY que el job de sincronizacion ya dejo en
    /// la libreta (<c>ExchangeRateQuotes</c>) nunca llegaba a mostrarse. Ahora, con el fetch en vivo
    /// fallando, se compara la FECHA del snapshot contra la FECHA de la libreta y gana la mas nueva.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenPersistedSnapshotIsOld_AndLibretaHasNewerRow_UsesLibreta()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Snapshot persistido VIEJO: del 8 de julio, exactamente el caso reportado por el dueño.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot() with { Value = 1510m, PublishedDate = "08/07/2026" });

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1345.75m,
                RateDate: new DateOnly(2026, 08, 05),
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 42,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Gana la libreta (mas nueva): NO el snapshot de julio.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1345.75m, dashboard.BnaUsdSellerRate!.Value);
        Assert.Equal("05/08/2026", dashboard.BnaUsdSellerRate.PublishedDate);
    }

    /// <summary>
    /// Contraparte de <see cref="Dashboard_WhenPersistedSnapshotIsOld_AndLibretaHasNewerRow_UsesLibreta"/>:
    /// cuando el snapshot persistido ES el mas nuevo (de hoy) y la libreta trae algo mas viejo (de
    /// ayer, ej. el job todavia no corrio hoy), gana el snapshot — el orden de siempre.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenPersistedSnapshotIsNewer_AndLibretaIsOlder_UsesPersistedSnapshot()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Snapshot persistido de HOY (05/08/2026).
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot() with { Value = 1500m, PublishedDate = "05/08/2026" });

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1490m,
                RateDate: new DateOnly(2026, 08, 04),
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: true,
                QuoteId: 41,
                FetchedAt: DateTime.UtcNow.AddDays(-1),
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Gana el snapshot (mas nuevo): NO la fila de ayer de la libreta.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1500m, dashboard.BnaUsdSellerRate!.Value);
        Assert.Equal("05/08/2026", dashboard.BnaUsdSellerRate.PublishedDate);
    }

    /// <summary>
    /// Hallazgo del review (no bloqueante, cerrado por deuda cero): el snapshot guarda su fecha como
    /// TEXTO scrapeado del sitio del BNA, y ese texto puede venir roto/imparseable (HTML cambiado,
    /// campo vacío, lo que sea). Decisión ya documentada en <c>PickNewestDollarSource</c>: sin una
    /// fecha confiable del snapshot, NO hay forma honesta de decir que es "más nuevo" que la libreta
    /// (que sí tiene una columna DATE) — gana la libreta. Este test fija ese comportamiento como
    /// deliberado, no tácito.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenPersistedSnapshotHasUnparseableDate_AndLibretaIsValid_UsesLibreta()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Snapshot persistido con PublishedDate BASURA (no matchea ninguno de los formatos esperados).
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot() with { Value = 1510m, PublishedDate = "fecha-invalida" });

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1345.75m,
                RateDate: new DateOnly(2026, 08, 05),
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 43,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Gana la libreta: no hay forma confiable de comparar contra una fecha de snapshot rota.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1345.75m, dashboard.BnaUsdSellerRate!.Value);
        Assert.Equal("05/08/2026", dashboard.BnaUsdSellerRate.PublishedDate);
    }

    /// <summary>
    /// Hallazgo del review (no bloqueante, cerrado por deuda cero): EMPATE exacto de fecha entre el
    /// snapshot persistido y la fila de la libreta. <c>PickNewestDollarSource</c> usa <c>&gt;</c>
    /// estricto (no <c>&gt;=</c>) a propósito: a igualdad de fecha gana el snapshot, el mismo orden
    /// que ya tenía la pantalla ANTES de esta obra (no cambiar nada quando las dos fuentes son del
    /// mismo día). Este test fija ese desempate.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenPersistedSnapshotAndLibretaHaveTheExactSameDate_UsesPersistedSnapshot()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Snapshot persistido de HOY (05/08/2026) — MISMA fecha que la fila de la libreta de abajo.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot() with { Value = 1500m, PublishedDate = "05/08/2026" });

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1490m,
                RateDate: new DateOnly(2026, 08, 05),
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 44,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Empate de fecha: gana el snapshot (orden de siempre), NO la libreta.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1500m, dashboard.BnaUsdSellerRate!.Value);
        Assert.Equal("05/08/2026", dashboard.BnaUsdSellerRate.PublishedDate);
    }

    // ============================================================
    // Ampliacion 2026-08-06 ("el euro y el real tampoco tienen que faltar"): EuroValue/RealValue del
    // DTO se resuelven con la MISMA regla "el dato mas nuevo gana", pero por moneda, y con un gate
    // extra: si el dato mas fresco de esa moneda es MAS VIEJO que la fecha del dolar que se esta
    // mostrando, se OCULTA en vez de mostrarse desactualizado (la tira solo tiene una fecha, la del
    // dolar — ver AttachFreshAuxiliaryCurrenciesAsync).
    // ============================================================

    /// <summary>
    /// El snapshot persistido es de AYER (dolar Y euro comparten la misma fecha de scrapeo — el DTO
    /// tiene UNA sola <c>PublishedDate</c> para toda la foto); la libreta tiene un dolar de HOY (gana
    /// para dolar, igual que <see cref="Dashboard_WhenPersistedSnapshotIsOld_AndLibretaHasNewerRow_UsesLibreta"/>)
    /// Y TAMBIEN un euro de HOY -> el euro de la libreta gana, porque es tan fresco como el dolar que
    /// se esta mostrando; el euro viejo del snapshot de ayer queda descartado.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenLibretaHasFreshEuroForTheSameDateAsTheDollarShown_ShowsTheLibretaEuro()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Snapshot persistido de AYER (dolar Y euro comparten esa fecha, es UNA sola foto).
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot() with
            {
                Value = 1490m,
                PublishedDate = "04/08/2026",
                EuroValue = 1400m,
            });

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1520m,
                RateDate: new DateOnly(2026, 08, 05), // MAS NUEVO que el snapshot de ayer -> gana la libreta para dolar.
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 50,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("EUR", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1731.60m,
                RateDate: new DateOnly(2026, 08, 05), // MISMA fecha que el dolar ganador (la libreta, no el snapshot).
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 51,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("BRL", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1520m, dashboard.BnaUsdSellerRate!.Value); // gano la libreta para dolar (mas nueva que el snapshot).
        // Gana la libreta para EURO tambien: es tan fresca como el dolar mostrado (05/08), el euro
        // viejo del snapshot (04/08) queda descartado.
        Assert.Equal(1731.60m, dashboard.BnaUsdSellerRate.EuroValue);
        Assert.Null(dashboard.BnaUsdSellerRate.RealValue);
    }

    /// <summary>
    /// Regla de frescura fijada (2026-08-06): el UNICO dato de euro disponible (ni el snapshot ni la
    /// libreta lo tienen fresco) es de HACE UN MES. El dolar que se esta mostrando es de HOY. En vez
    /// de mostrar un euro viejo sin ninguna forma de avisarlo (la tira solo tiene una fecha), se
    /// OCULTA — jamas un euro de hace un mes al lado de un dolar de hoy sin que se note.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenTheOnlyEuroDataIsOlderThanTheDollarShown_HidesTheEuro()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Sin snapshot persistido en absoluto: la UNICA fuente posible de euro es la libreta.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1520m,
                RateDate: new DateOnly(2026, 08, 05), // dolar de HOY.
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 60,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("EUR", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1600m,
                RateDate: new DateOnly(2026, 07, 05), // un mes atras.
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: true,
                QuoteId: 61,
                FetchedAt: DateTime.UtcNow.AddDays(-31),
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("BRL", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1520m, dashboard.BnaUsdSellerRate!.Value);
        // El euro de hace un mes NO se muestra al lado del dolar de hoy: se oculta, no se avisa con
        // texto (el DTO no tiene donde poner una segunda fecha) — la ausencia ES el aviso.
        Assert.Null(dashboard.BnaUsdSellerRate.EuroValue);
        Assert.Null(dashboard.BnaUsdSellerRate.RealValue);
    }

    /// <summary>
    /// Tercer borde del gate de frescura (hallazgo de review 2026-08-06, fija el caso que faltaba: ya
    /// estaban cubiertos el empate — <see cref="Dashboard_WhenPersistedSnapshotAndLibretaHaveTheExactSameDate_UsesPersistedSnapshot"/>
    /// vía dólar — y el más-viejo — <see cref="Dashboard_WhenTheOnlyEuroDataIsOlderThanTheDollarShown_HidesTheEuro"/>).
    /// Acá el euro de la libreta es MÁS NUEVO que la fecha del dólar que se está mostrando (el dólar
    /// quedó viejo, pero el euro sí se actualizó hoy) -> el euro SÍ se muestra: la regla es "al menos
    /// tan fresco como el dólar", nunca "exactamente la misma fecha".
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenEuroIsNewerThanTheDollarShown_ShowsTheEuro()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // Sin snapshot persistido: el dolar sale de la libreta, con una fecha VIEJA (ayer).
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1490m,
                RateDate: new DateOnly(2026, 08, 04), // dolar VIEJO: el job todavia no trajo el de hoy.
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: true,
                QuoteId: 70,
                FetchedAt: DateTime.UtcNow.AddDays(-1),
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("EUR", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1731.60m,
                RateDate: new DateOnly(2026, 08, 05), // euro MAS NUEVO que el dolar mostrado (04/08).
                Source: ExchangeRateSource.OficialPorApi,
                ProviderName: "dolarapi",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 71,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));
        resolver
            .Setup(r => r.GetSuggestionAsync("BRL", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1490m, dashboard.BnaUsdSellerRate!.Value); // dolar viejo (unico dato disponible).
        // El euro, aunque el dolar de al lado sea viejo, ES mas nuevo que esa fecha -> se muestra.
        Assert.Equal(1731.60m, dashboard.BnaUsdSellerRate.EuroValue);
        Assert.Null(dashboard.BnaUsdSellerRate.RealValue);
    }

    /// <summary>
    /// Camino feliz mas comun: el fetch en vivo del BNA funciona (IsStale=false) y ya trae euro/real
    /// de la MISMA pagina que el dolar — el dashboard los muestra TAL CUAL, sin consultar la libreta
    /// para nada (son, por definicion, tan frescos como el dolar que viene en el mismo objeto).
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenLiveBnaFetchSucceeds_ShowsItsOwnEuroAndReal_WithoutConsultingTheResolver()
    {
        await using var context = CreateContext();

        var live = PersistedSnapshot() with { Value = 1515m, EuroValue = 1660m, RealValue = 268m, IsStale = false };

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(live);

        var resolver = new Mock<IExchangeRateResolver>(MockBehavior.Strict);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1660m, dashboard.BnaUsdSellerRate!.EuroValue);
        Assert.Equal(268m, dashboard.BnaUsdSellerRate.RealValue);
        // MockBehavior.Strict + cero Setup en el resolver: si el codigo llegara a llamarlo, el test
        // fallaria por una excepcion de "llamada no configurada" en vez de silenciosamente pasar.
    }
}
