using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A3 + A4): lo que hace el MOTOR al
/// emitir una factura en dolares.
///
/// <para>Las dos ayudas que se prueban aca, en criollo:</para>
/// <list type="number">
///   <item><b>El techo (A4)</b>: si el vendedor declara un dolar mas alto del que la factura admite, el
///   motor lo baja al maximo y emite igual — antes el comprobante rebotaba con un error que el vendedor
///   no sabia arreglar. Lo que el habia querido poner queda guardado en el rastro interno.</item>
///   <item><b>El completado automatico (A3)</b>: mientras el sistema emite comprobantes de ensayo, la
///   pantalla ni dibuja el casillero y no manda ningun numero; lo pone el motor.</item>
/// </list>
///
/// <para>Tests UNITARIOS (InMemory + Moq): no tocan ARCA ni Postgres.</para>
/// </summary>
public class TcAyudaInvisibleEmisionTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IBackgroundJobClient> _jobClientMock = new();
    private readonly Mock<IAfipService> _afipMock = new();
    private readonly Mock<IInvoicePdfService> _pdfMock = new();
    private readonly Mock<IExchangeRateResolver> _resolverMock = new();

    private static readonly Guid ReservaPublicId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly FechaDelDato = new(2026, 08, 06);

    public TcAyudaInvisibleEmisionTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        // Por defecto: sin sugerencia y sin techo (libreta vacia). Cada test configura lo suyo.
        _resolverMock
            .Setup(r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((ExchangeRateSuggestion?)null);
        _resolverMock
            .Setup(r => r.GetInvoicingCeilingAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);
    }

    // ============================================================
    // Andamiaje
    // ============================================================

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <param name="resolver">
    /// Por defecto el resolver MOCKEADO (cada test arma la sugerencia y el techo que necesita). Los
    /// tests del candado a prueba de fallas pasan el resolver REAL a propósito: ahí lo que se prueba es
    /// el acoplamiento configuración -&gt; memoria -&gt; sugerencia, que con un mock no existiría.
    /// </param>
    private InvoiceService BuildInvoiceService(
        AppDbContext context,
        out List<CreateInvoiceRequest> capturedRequests,
        IExchangeRateResolver? resolver = null)
    {
        var settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableMultiCurrencyInvoicing = true });

        var captured = new List<CreateInvoiceRequest>();
        capturedRequests = captured;

        _afipMock
            .Setup(s => s.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => captured.Add(req))
            .ReturnsAsync(new Invoice { Id = 777, ReservaId = 1, TipoComprobante = 6, Resultado = "PENDING" });

        return new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            _afipMock.Object,
            _pdfMock.Object,
            _mapper,
            _jobClientMock.Object,
            NullLogger<InvoiceService>.Instance,
            settingsServiceMock.Object,
            BuildUserManager(),
            permissionResolver: null,
            httpContextAccessor: null,
            approvalService: null,
            approvalPolicyService: null,
            serviceProvider: null,
            exchangeRateResolver: resolver ?? _resolverMock.Object);
    }

    /// <summary>
    /// Igual que <see cref="BuildInvoiceService"/>, pero con el <c>AfipService</c> DE VERDAD: hace falta
    /// cuando el test tiene que llegar hasta la Invoice guardada (el copiado request -&gt; entidad vive
    /// dentro de <c>CreatePendingInvoice</c>, y con el mock nunca se ejecuta).
    /// </summary>
    private InvoiceService BuildInvoiceServiceWithRealAfipService(
        AppDbContext context, IExchangeRateResolver? resolver = null)
    {
        var settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableMultiCurrencyInvoicing = true });

        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(p => p.UnprotectString(It.IsAny<string?>())).Returns((string? v) => v);
        protector.Setup(p => p.UnprotectBytes(It.IsAny<byte[]?>())).Returns((byte[]? v) => v);

        var afipService = new AfipService(
            context, NullLogger<AfipService>.Instance, new HttpClient(), protector.Object);

        return new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            afipService,
            _pdfMock.Object,
            _mapper,
            _jobClientMock.Object,
            NullLogger<InvoiceService>.Instance,
            settingsServiceMock.Object,
            BuildUserManager(),
            permissionResolver: null,
            httpContextAccessor: null,
            approvalService: null,
            approvalPolicyService: null,
            serviceProvider: null,
            exchangeRateResolver: resolver ?? _resolverMock.Object);
    }

    private static async Task SeedSettledReservaAsync(AppDbContext context)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = ReservaPublicId,
            NumeroReserva = "F-TC-0001",
            Name = "Reserva en dólares",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1000m,
            Balance = 0m,
            TotalPaid = 1000m
        });
        await context.SaveChangesAsync();
    }

    private static CreateInvoiceRequest BuildUsdRequest(decimal monCotiz, string? justificacion = null) => new()
    {
        ReservaId = ReservaPublicId.ToString(),
        MonId = "USD",
        MonCotiz = monCotiz,
        ExchangeRateJustification = justificacion,
        Items = new List<InvoiceItemDto>
        {
            new() { Description = "Paquete", Quantity = 1, UnitPrice = 1000m, Total = 1000m, AlicuotaIvaId = 3 }
        }
    };

    private void SetupSuggestion(decimal rate, bool esDeEnsayo, int quoteId = 55)
    {
        _resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: rate,
                RateDate: FechaDelDato,
                Source: ExchangeRateSource.AfipOficial,
                ProviderName: "ARCA_WSFEv1",
                ArcaFchCotiz: FechaDelDato,
                IsStale: false,
                QuoteId: quoteId,
                FetchedAt: new DateTime(2026, 08, 06, 12, 0, 0, DateTimeKind.Utc),
                IsProductionSource: !esDeEnsayo));
    }

    private void SetupCeiling(decimal ceiling)
    {
        _resolverMock
            .Setup(r => r.GetInvoicingCeilingAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ceiling);
    }

    // ============================================================
    // A4 — el techo: se acomoda solo y deja rastro
    // ============================================================

    /// <summary>
    /// REGRESIÓN del payload REAL de la pantalla (acordado con el front, 2026-08-06): la pantalla manda
    /// en el tipo de cambio el número ORIGINAL que tipeó el usuario, tal cual, y NO manda explicación
    /// (porque a este caso no se le pide ninguna). El acomodo y el rastro viven SOLO acá.
    ///
    /// <para>Es el test que protege el contrato entre las dos puntas: si mañana el front empezara a
    /// mandar el número ya acomodado, este caso dejaría de ejercitarse y el motor quedaría sin
    /// cobertura del único camino donde pisa un dato del usuario.</para>
    /// </summary>
    [Fact]
    public async Task PayloadRealDeLaPantalla_ArribaDelTechoYSinExplicacion_EmiteAcomodado()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out var captured);

        // Exactamente lo que manda la pantalla: el número tipeado, sin explicación.
        await service.CreateAsync(BuildUsdRequest(1500m), "user-1", "Vendedora", CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(1235.50m, request.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.ClampedToDailyCeiling, request.ExchangeRateOrigin);
        Assert.Equal(1500m, request.RequestedExchangeRate);
    }

    /// <summary>
    /// El rastro no sirve de nada si se queda en el request: este test llega hasta la Invoice
    /// PERSISTIDA, con el <c>AfipService</c> DE VERDAD (no el mock), porque el copiado
    /// request -&gt; entidad vive ahí y hasta hoy no lo cubría nada.
    /// </summary>
    [Fact]
    public async Task ElRastroDelAcomodo_LlegaHastaLaFacturaGuardada()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        context.AfipSettings.Add(new AfipSettings
        {
            Cuit = 20111111112,
            PuntoDeVenta = 1,
            TaxCondition = "Responsable Inscripto",
            IsProduction = false
        });
        await context.SaveChangesAsync();

        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceServiceWithRealAfipService(context);

        await service.CreateAsync(BuildUsdRequest(1500m), "user-1", "Vendedora", CancellationToken.None);

        var persistida = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(1235.50m, persistida.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.ClampedToDailyCeiling, persistida.ExchangeRateOrigin);
        Assert.Equal(1500m, persistida.RequestedExchangeRate);
        Assert.Equal("DOL", persistida.MonId);
    }

    [Fact]
    public async Task ArribaDelTecho_SeAcomodaSolo_YGuardaLoQueElUsuarioQuisoPoner()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out var captured);

        // El vendedor escribe el dolar al que cobro de verdad. Antes esto rebotaba despues de
        // apretar "Emitir"; ahora el motor lo baja al maximo y emite.
        await service.CreateAsync(BuildUsdRequest(1500m), "user-1", "Vendedora", CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(1235.50m, request.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.ClampedToDailyCeiling, request.ExchangeRateOrigin);
        // El rastro: lo que el usuario quiso poner no se pierde.
        Assert.Equal(1500m, request.RequestedExchangeRate);
        Assert.Null(request.ExchangeRateQuoteId);
        // Nunca se le pidio una explicacion (el numero que quedo no lo eligio el), pero el
        // comprobante igual queda explicado: la escribe el motor.
        Assert.False(string.IsNullOrWhiteSpace(request.ExchangeRateJustification));
        Assert.Contains("1.500,00", request.ExchangeRateJustification!);
        Assert.Contains("1.235,50", request.ExchangeRateJustification!);
    }

    /// <summary>
    /// Tipear EXACTAMENTE el techo a mano es legítimo y frecuente (el vendedor ve la línea gris "En la
    /// factura entra hasta $ X" y escribe ese número). No hay nada que acomodar, así que el motor NO
    /// deja rastro de acomodo — pero sigue siendo carga a mano: el número no es el que sugirió el
    /// sistema, así que la pantalla SÍ le pidió de dónde lo sacó y el motor exige esa explicación.
    /// </summary>
    [Fact]
    public async Task JustoEnElTecho_EscritoAMano_NoSeAcomoda_YSigueSiendoCargaAMano()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out var captured);

        await service.CreateAsync(
            BuildUsdRequest(1235.50m, justificacion: "Es el dólar al que cobré."),
            "user-1", "Vendedora", CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(1235.50m, request.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.ManualWithJustification, request.ExchangeRateOrigin);
        Assert.Null(request.RequestedExchangeRate);
        // La explicación que escribió el usuario NO se toca: el motor solo escribe la suya cuando
        // acomoda al techo, y acá no acomodó nada.
        Assert.Equal("Es el dólar al que cobré.", request.ExchangeRateJustification);
    }

    /// <summary>
    /// Contracara del de arriba: justo en el techo pero SIN explicación, el motor rechaza. Es la prueba
    /// de que el caso "escribí el techo a mano" pasa por el candado normal de carga a mano (INV-120) y
    /// no se cuela por la puerta del acomodo automático.
    /// </summary>
    [Fact]
    public async Task JustoEnElTecho_SinExplicacion_SeRechaza()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(BuildUsdRequest(1235.50m), "user-1", "Vendedora", CancellationToken.None));
    }

    /// <summary>
    /// Sin techo conocido (el organismo todavia no publico ese dia) el motor NO acomoda nada: es
    /// preferible el comportamiento de siempre a bajarle el valor a una factura por las dudas.
    /// </summary>
    [Fact]
    public async Task SinTechoConocido_NoSeAcomodaNada()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, out var captured);

        await service.CreateAsync(
            BuildUsdRequest(1500m, justificacion: "Cotización que me pasó el operador."),
            "user-1", "Vendedora", CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(1500m, request.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.ManualWithJustification, request.ExchangeRateOrigin);
        Assert.Null(request.RequestedExchangeRate);
    }

    // ============================================================
    // A3 — el motor completa el numero solo
    // ============================================================

    [Fact]
    public async Task CuandoLoCompletaElSistema_ElFrontNoMandaNumero_YElMotorLoPone()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1152.202m, esDeEnsayo: true);

        var service = BuildInvoiceService(context, out var captured);

        // La pantalla no dibuja el casillero, asi que no manda tipo de cambio: llega el 1 por
        // defecto del contrato. Sin esta ayuda, el guard de "un dólar no vale 1 peso" lo rechazaba.
        var request = BuildUsdRequest(monCotiz: 1m);
        await service.CreateAsync(request, "user-1", "Vendedora", CancellationToken.None);

        var captura = Assert.Single(captured);
        Assert.Equal(1152.202m, captura.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.SystemFilled, captura.ExchangeRateOrigin);
        Assert.Equal(ExchangeRateSource.AfipOficial, captura.ExchangeRateSource);
        Assert.Equal(55, captura.ExchangeRateQuoteId);
        // No se le pidio explicacion a nadie: no hubo numero que pisar.
        Assert.Null(captura.ExchangeRateJustification);
    }

    /// <summary>
    /// Aunque el usuario (o un formulario viejo) mande un numero, en este modo el motor lo pisa: el
    /// comprobante solo entra con el numero que el organismo exige. Es lo que hace que nunca rebote.
    /// </summary>
    [Fact]
    public async Task CuandoLoCompletaElSistema_PisaCualquierNumeroQueLlegue()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1152.202m, esDeEnsayo: true);

        var service = BuildInvoiceService(context, out var captured);

        await service.CreateAsync(
            BuildUsdRequest(1500m, justificacion: "sobra"),
            "user-1", "Vendedora", CancellationToken.None);

        var captura = Assert.Single(captured);
        Assert.Equal(1152.202m, captura.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.SystemFilled, captura.ExchangeRateOrigin);
        Assert.Null(captura.ExchangeRateJustification);
    }

    // ============================================================
    // El camino normal sigue igual
    // ============================================================

    [Fact]
    public async Task AceptaLaSugerenciaTalCual_QuedaMarcadoComoSugeridaAceptada_YNoPideExplicacion()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false, quoteId: 88);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out var captured);

        await service.CreateAsync(BuildUsdRequest(1234.50m), "user-1", "Vendedora", CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(1234.50m, request.MonCotiz);
        Assert.Equal(InvoiceExchangeRateOrigin.SuggestedAccepted, request.ExchangeRateOrigin);
        Assert.Equal(88, request.ExchangeRateQuoteId);
        Assert.Null(request.RequestedExchangeRate);
    }

    /// <summary>
    /// Debajo del techo pero distinto de la sugerencia: sigue siendo carga a mano y sigue exigiendo la
    /// explicacion escrita (invariante INV-120). Esta ayuda no aflojo ese candado.
    /// </summary>
    [Fact]
    public async Task DebajoDelTechoYSinExplicacion_SeRechaza()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        SetupSuggestion(1234.50m, esDeEnsayo: false);
        SetupCeiling(1235.50m);

        var service = BuildInvoiceService(context, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(BuildUsdRequest(1200m), "user-1", "Vendedora", CancellationToken.None));
    }

    // ============================================================
    // Candado a prueba de fallas: emitiendo comprobantes REALES, el motor nunca completa solo
    // ============================================================

    /// <summary>
    /// Hallazgo de SEGURIDAD B1 (2026-08-06). El escenario real, en criollo: la agencia venia emitiendo
    /// comprobantes de ensayo, el admin la pasa a emitir comprobantes REALES, y durante unos minutos el
    /// sistema seguia creyendo que estaba en ensayo (tiene ese dato guardado en memoria para no
    /// consultarlo mil veces por minuto). En esa ventana, el motor completaba el tipo de cambio solo con
    /// el número de juguete — y salía un comprobante REAL, con CAE, mal valuado y sin vuelta atrás.
    ///
    /// <para><b>Este test NO simula la marca "es de ensayo" a mano</b>: arma el acoplamiento de verdad
    /// (configuración guardada -&gt; dato en memoria -&gt; sugerencia) con el resolver REAL, calienta la
    /// memoria estando en ensayo, cambia la configuración por atrás y recién ahí emite. Es la única
    /// forma de que el test falle si alguien saca el candado.</para>
    /// </summary>
    [Fact]
    public async Task SiLaAgenciaYaEmiteComprobantesReales_ElMotorNoCompletaAunqueLaMemoriaDigaLoContrario()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        // 1) La agencia arranca emitiendo comprobantes de ensayo, y la libreta tiene el número que el
        //    organismo exige en ese modo.
        context.AfipSettings.Add(new AfipSettings
        {
            Cuit = 20111111112,
            PuntoDeVenta = 1,
            TaxCondition = "Responsable Inscripto",
            IsProduction = false
        });
        var hoy = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
        context.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = hoy,
            Source = ExchangeRateSource.AfipOficial,
            Rate = 1152.202m,
            ProviderName = "ARCA_WSFEv1",
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = hoy,
            IsProductionSource = false
        });
        await context.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolverReal = new ExchangeRateResolver(context, cache, NullLogger<ExchangeRateResolver>.Instance);

        // 2) Alguien abre la pantalla de facturar: el resolver deja el entorno "de ensayo" guardado en
        //    memoria por 5 minutos.
        var sugerencia = await resolverReal.GetSuggestionAsync("USD", hoy, CancellationToken.None);
        Assert.NotNull(sugerencia);
        Assert.True(sugerencia!.LoCompletaElSistema);

        // 3) El admin pasa la agencia a emitir comprobantes REALES. La memoria NO se entera (es
        //    justamente la ventana peligrosa que este test reproduce).
        var settings = await context.AfipSettings.SingleAsync();
        settings.IsProduction = true;
        await context.SaveChangesAsync();
        Assert.True((await resolverReal.GetSuggestionAsync("USD", hoy, CancellationToken.None))!.LoCompletaElSistema);

        // 4) Se emite. El motor NO puede autocompletar: relee el entorno de la base sin pasar por la
        //    memoria. Como la pantalla no había dibujado el casillero, no llega ningún tipo de cambio y
        //    la emisión se corta con un mensaje que le sirve al usuario.
        var service = BuildInvoiceService(context, out var captured, resolverReal);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(BuildUsdRequest(1m), "user-1", "Vendedora", CancellationToken.None));

        Assert.Equal(
            "No pudimos completar el tipo de cambio en este momento. Probá de nuevo en unos minutos.",
            ex.Message);
        Assert.Empty(captured);
    }

    /// <summary>
    /// Cierre del mismo hallazgo por el otro lado: al guardar la configuración de facturación, el dato
    /// guardado en memoria se borra. Sin esto, el candado de arriba salvaría la emisión pero el resto
    /// del sistema (la pantalla, la tira del inicio) seguiría mostrando el modo viejo varios minutos.
    /// </summary>
    [Fact]
    public async Task AlGuardarLaConfiguracionDeFacturacion_SeBorraElEntornoGuardadoEnMemoria()
    {
        using var context = new AppDbContext(_dbOptions);
        context.AfipSettings.Add(new AfipSettings
        {
            Cuit = 20111111112,
            PuntoDeVenta = 1,
            TaxCondition = "Responsable Inscripto",
            IsProduction = false
        });
        await context.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(ExchangeRateResolver.IsProductionCacheKey, false);

        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(p => p.UnprotectString(It.IsAny<string?>())).Returns((string? v) => v);
        protector.Setup(p => p.UnprotectBytes(It.IsAny<byte[]?>())).Returns((byte[]? v) => v);

        var afipService = new AfipService(
            context, NullLogger<AfipService>.Instance, new HttpClient(), protector.Object, auditService: null,
            memoryCache: cache);

        await afipService.UpdateSettingsAsync(
            cuit: 20111111112, puntoDeVenta: 1, isProduction: true, taxCondition: "Responsable Inscripto",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.False(cache.TryGetValue(ExchangeRateResolver.IsProductionCacheKey, out _));
    }

    /// <summary>
    /// Hallazgo de EXPOSICIÓN B1 (2026-08-06), caso "A3 desfasado": el vendedor abrió la pantalla cuando
    /// el motor iba a completar el tipo de cambio solo (no vio ningún casillero), y para cuando apretó
    /// "Emitir" el motor ya no podía — acá porque la cotización que iba a usar desapareció de la libreta.
    ///
    /// <para>Nunca escribió un número, así que decirle "revisá el tipo de cambio" sería culparlo de algo
    /// que no hizo. Lo único honesto y útil es "no pudimos, probá de nuevo".</para>
    /// </summary>
    [Fact]
    public async Task SiLaPantallaNoLeMostroCasillero_YElMotorNoPuedeCompletar_ElMensajeNoLoCulpa()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        // Sin sugerencia y sin techo (es el estado por defecto de los mocks): la libreta se quedó sin el
        // dato que la pantalla había visto un rato antes.

        var service = BuildInvoiceService(context, out var captured);

        // El request tal cual lo manda la pantalla en ese modo: sin tipo de cambio.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(BuildUsdRequest(1m), "user-1", "Vendedora", CancellationToken.None));

        Assert.Equal(
            "No pudimos completar el tipo de cambio en este momento. Probá de nuevo en unos minutos.",
            ex.Message);
        Assert.Empty(captured);
    }

    /// <summary>
    /// La otra rama del mismo hallazgo: un comprobante que NO pasa por el motor de sugerencias (una nota
    /// de crédito hereda el tipo de cambio del comprobante que corrige, nunca lo recotiza) y trae un
    /// valor heredado que no sirve. Ahí sí corresponde pedir que lo revisen — pero en criollo, sin
    /// volcarle el número ni la regla interna entre paréntesis como hacía antes.
    /// </summary>
    [Fact]
    public async Task ConUnComprobanteHeredadoQueTraeUnTipoDeCambioInservible_ElMensajeNoVuelcaNadaTecnico()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, out _);

        var notaDeCredito = BuildUsdRequest(1m);
        notaDeCredito.IsCreditNote = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(notaDeCredito, "user-1", "Vendedora", CancellationToken.None));

        Assert.Equal("Revisá el tipo de cambio: no podés facturar en dólares con ese valor.", ex.Message);
        Assert.DoesNotContain("incoherente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USD", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cotizacion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
