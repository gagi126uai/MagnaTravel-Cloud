using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-012 MVP (facturar en dolares, 2026-05-29): tests focales del primer paso de
/// facturacion multimoneda. Cubren las dos piezas nuevas:
///
/// <list type="bullet">
///   <item><b>InvoiceService.ValidateMultiCurrencyInvoicingAsync</b> (gate de validacion):
///   flag OFF byte-identico, flag ON con USD valido pasa y forwardea los 3 campos de TC,
///   flag ON con cotizacion incoherente o sin justificacion rechaza.</item>
///   <item><b>AfipService.CreatePendingInvoice</b> (poblado + A/B/C): con USD valido puebla
///   las columnas de trazabilidad del TC; la decision A/B/C sigue igual aunque sea en USD
///   (la moneda es ortogonal al tipo de comprobante).</item>
/// </list>
///
/// <para>Son tests UNITARIOS (InMemory + Moq), NO de integracion: no tocan ARCA ni Postgres.</para>
/// </summary>
public class MultiCurrencyInvoicingTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IBackgroundJobClient> _jobClientMock = new();
    private readonly Mock<IAfipService> _afipMock = new();
    private readonly Mock<IInvoicePdfService> _pdfMock = new();
    // ADR-011 (enmienda 2026-08-05): resolver mockeado. Por defecto devuelve null (simula la
    // libreta ExchangeRateQuotes vacia, el estado real el dia 1 del Deploy 1 antes de que el job
    // corra) — asi cualquier request en USD que no matchee EXACTO cae a Manual, que es el
    // comportamiento correcto §8.2. Los tests que necesitan una sugerencia que SI matchea la
    // reconfiguran puntualmente.
    private readonly Mock<IExchangeRateResolver> _exchangeRateResolverMock = new();

    public MultiCurrencyInvoicingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _exchangeRateResolverMock
            .Setup(r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExchangeRateSuggestion?)null);
    }

    // ============================================================
    // Infraestructura comun
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

    /// <summary>
    /// Arma un InvoiceService con el flag multimoneda en el estado pedido. El mock de
    /// IAfipService captura el CreateInvoiceRequest que recibe para que el test pueda
    /// verificar que la validacion lo dejo pasar con los campos esperados.
    /// </summary>
    private InvoiceService BuildInvoiceService(
        AppDbContext context,
        bool enableMultiCurrency,
        out List<CreateInvoiceRequest> capturedRequests,
        // ADR-011: por defecto SI se cablea el resolver mockeado (comportamiento real de
        // produccion, donde Program.cs siempre lo registra). Pasar null explicito reproduce el
        // camino legacy sin resolver (solo deberia usarse para blindar ese fallback puntual).
        bool wireExchangeRateResolver = true)
    {
        var settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableMultiCurrencyInvoicing = enableMultiCurrency
            });

        var captured = new List<CreateInvoiceRequest>();
        capturedRequests = captured;

        // El mock devuelve una Invoice minima: solo nos importa que la validacion previa
        // dejo (o no) llegar el request aca. El comportamiento real de CreatePendingInvoice
        // se cubre en los tests de AfipService de mas abajo.
        _afipMock
            .Setup(s => s.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => captured.Add(req))
            .ReturnsAsync(new Invoice { Id = 999, ReservaId = 1, TipoComprobante = 6, Resultado = "PENDING" });

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
            exchangeRateResolver: wireExchangeRateResolver ? _exchangeRateResolverMock.Object : null);
    }

    // PublicId fijo de la reserva: el service resuelve ReservaId del request como PublicId
    // (Guid), no como Id interno. Lo compartimos entre el seed y el request.
    private static readonly Guid ReservaPublicId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task SeedSettledReservaAsync(AppDbContext context)
    {
        // Reserva sin deuda (Balance 0) para que el guard de pago no interfiera: el foco
        // de estos tests es la validacion de moneda, no la de saldo.
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = ReservaPublicId,
            NumeroReserva = "F-ADR012-001",
            Name = "Reserva multimoneda",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1000m,
            Balance = 0m,
            TotalPaid = 1000m
        });
        await context.SaveChangesAsync();
    }

    private static CreateInvoiceRequest BuildBaseRequest() => new()
    {
        ReservaId = ReservaPublicId.ToString(),
        IsCreditNote = false,
        IsDebitNote = false,
        Items = new List<InvoiceItemDto>
        {
            new() { Description = "Hotel", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 }
        }
    };

    // ============================================================
    // (a) Flag OFF -> byte-identico: ignora la moneda, deja pasar el request tal cual.
    // ============================================================

    [Fact]
    public async Task FlagOff_WithForeignCurrency_DoesNotValidate_AndForwardsRequestAsIs()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: false, out var captured);

        // Mandamos un request "mal" (USD con cotizacion 1, sin justificacion). Con el flag
        // OFF no debe validar nada: el request llega intacto a CreatePendingInvoice, igual
        // que hoy. (CreatePendingInvoice esta mockeado, asi que no convierte a pesos aca;
        // lo que importa es que la validacion NO lanzo.)
        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1m;

        await service.CreateAsync(request, userId: "u1", userName: "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal("DOL", captured[0].MonId);
    }

    // ============================================================
    // (b) Flag ON + USD sin sugerencia disponible (libreta vacia) + justificacion -> pasa como
    //     Manual y forwardea. ADR-011 cambia el comportamiento: antes de esta obra el request
    //     mandaba la fuente y el service la dejaba pasar tal cual; ahora el SERVIDOR decide (§8.2)
    //     e IGNORA lo que mando el request (test 19 de la spec) — sin sugerencia que matchee,
    //     siempre es Manual. (El poblado real de columnas se verifica en el test de AfipService de
    //     abajo, y el camino "matchea la sugerencia" en los tests ADR-011 mas abajo.)
    // ============================================================

    [Fact]
    public async Task FlagOn_SinSugerenciaDisponible_PasaComoManual_IgnorandoLaFuenteDelRequest()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;
        // El front (viejo o nuevo, da igual) manda una fuente inventada: el servidor la ignora.
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC vendedor divisa BNA dia habil anterior (RG 5616).";

        await service.CreateAsync(request, userId: "u1", userName: "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal("DOL", captured[0].MonId);
        Assert.Equal(1234.56m, captured[0].MonCotiz);
        Assert.Equal(ExchangeRateSource.Manual, captured[0].ExchangeRateSource);
        Assert.Null(captured[0].ExchangeRateQuoteId);
        Assert.NotNull(captured[0].ExchangeRateFetchedAt);
        Assert.False(string.IsNullOrWhiteSpace(captured[0].ExchangeRateJustification));
    }

    // ============================================================
    // (b.2) Normalizacion ISO -> ARCA (ADR-012 fix, 2026-05-29).
    //   El caller puede mandar el codigo en ISO ("USD") o ya en ARCA ("DOL"). El job de
    //   emision solo acepta ARCA, asi que el gate DEBE normalizar antes de persistir.
    //   Verificamos que el MonId que llega a CreatePendingInvoice (= el que se persiste)
    //   sea siempre el codigo ARCA.
    // ============================================================

    [Fact]
    public async Task FlagOn_WithIsoCurrencyUsd_NormalizesMonIdToArcaDol()
    {
        // (a) Caller manda ISO "USD" -> la factura debe persistir con "DOL" (codigo ARCA),
        // si no el job (que valida con IsValidArcaCurrencyCode) la rechazaria colgada.
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "USD"; // ISO 4217, NO codigo ARCA
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC vendedor divisa BNA (RG 5616).";

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        // El request que llega a CreatePendingInvoice ya viene normalizado a ARCA.
        Assert.Equal("DOL", captured[0].MonId);
    }

    [Fact]
    public async Task FlagOn_WithArcaCurrencyDol_KeepsMonIdAsDol()
    {
        // (b) Caller manda ya el codigo ARCA "DOL" -> se acepta y queda "DOL" (no se rompe
        // por intentar mapearlo como ISO).
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL"; // ya en formato ARCA
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC vendedor divisa BNA (RG 5616).";

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal("DOL", captured[0].MonId);
    }

    [Fact]
    public async Task FlagOn_WithUnsupportedCurrency_Throws()
    {
        // (c) Moneda no soportada ("EUR", ni ISO mapeable ni codigo ARCA valido) -> rechaza
        // antes de crear la factura. Evita dejar colgada una PENDING que el job rechazaria.
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "EUR"; // no soportada todavia
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC manual.";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));

        Assert.Empty(captured);
    }

    // ============================================================
    // (c) Flag ON + USD con MonCotiz == 1 o <= 0 -> rechaza.
    // ============================================================

    [Theory]
    [InlineData(1)]      // un dolar no vale 1 peso
    [InlineData(0)]      // cotizacion nula
    [InlineData(-5)]     // cotizacion negativa
    public async Task FlagOn_WithIncoherentExchangeRate_Throws(int monCotiz)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = monCotiz;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC manual.";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));

        // No debe haber llegado a crear la factura PENDING.
        Assert.Empty(captured);
    }

    // ============================================================
    // (d) Flag ON + USD sin justificacion (o sin fuente / sin fecha) -> rechaza.
    // ============================================================

    [Fact]
    public async Task FlagOn_WithoutJustification_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "   "; // vacio/whitespace -> invalido

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));

        Assert.Empty(captured);
    }

    [Fact]
    public async Task FlagOn_WithoutSource_Throws()
    {
        // ADR-011: con el resolver CABLEADO (comportamiento real de produccion), una factura de
        // venta genuina SIEMPRE termina con Source resuelto por el servidor (AfipOficial o Manual) —
        // el concepto de "el request no trajo fuente" deja de poder ocurrir para ese camino. Este
        // guard sigue vivo por el camino LEGACY sin resolver (que en produccion nunca pasa, T-11 sin
        // flag) y por NC/ND con datos heredados incompletos; lo probamos aca con
        // wireExchangeRateResolver:false para ejercitar exactamente ESE fallback.
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured, wireExchangeRateResolver: false);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = null; // falta fuente
        request.ExchangeRateFetchedAt = DateTime.UtcNow;
        request.ExchangeRateJustification = "TC manual.";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));

        Assert.Empty(captured);
    }

    [Fact]
    public async Task FlagOn_WithPesos_PassesWithoutRequiringTraceFields()
    {
        // Sanity: con el flag ON pero factura en pesos, NO debe exigir TC ni justificacion.
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest(); // MonId default "PES", sin campos de TC

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
    }

    // ============================================================
    // ADR-011 fix BLOQUEANTE F-4 (revision post-implementacion 2026-08-05): la procedencia del TC
    // NUNCA se confia al request. Dos capas de defensa, un test por capa:
    //   1) limpieza incondicional en InvoiceService.CreateAsync (venta genuina, CUALQUIER flag/moneda);
    //   2) [JsonIgnore] en CreateInvoiceRequest (bloquea el binding HTTP incluso para NC/ND, que la
    //      capa 1 deliberadamente NO limpia porque la NC/ND SI hereda QuoteId legitimamente — ver
    //      el test Adr011_NotaDeCredito_HeredaElTcDelOriginal_YNuncaLlamaAlResolver mas abajo).
    // ============================================================

    /// <summary>
    /// Capa 1: aunque el objeto YA traiga un Source/QuoteId "inventado" (simula que, por el motivo
    /// que sea, algo los dejo poblados antes de llamar CreateAsync), con el flag OFF una factura de
    /// venta genuina en USD queda SIN esa procedencia falsa — CreateAsync los limpia ANTES de que
    /// ValidateMultiCurrencyInvoicingAsync (que con el flag OFF ni siquiera corre) llegue a mirarlos.
    /// </summary>
    [Fact]
    public async Task Adr011_FlagOff_ConSourceYQuoteIdInventados_LaFacturaQuedaSinProcedenciaFalsa()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: false, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1m; // "malo" a proposito: con el flag OFF nada lo valida, pero tampoco importa.
        // Simula un ExchangeRateQuoteId que NO corresponde a nada real de la libreta.
        request.ExchangeRateSource = ExchangeRateSource.AfipOficial;
        request.ExchangeRateQuoteId = 999999;
        request.ExchangeRateFchCotiz = new DateOnly(2020, 01, 01);
        request.ExchangeRateFetchedAt = DateTime.UtcNow;

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Null(captured[0].ExchangeRateSource);
        Assert.Null(captured[0].ExchangeRateQuoteId);
        Assert.Null(captured[0].ExchangeRateFchCotiz);
        Assert.Null(captured[0].ExchangeRateFetchedAt);
    }

    /// <summary>
    /// Capa 2: para el camino de NC/ND (donde CreateAsync deliberadamente NO limpia QuoteId, porque
    /// la NC/ND legitima lo hereda del original), la unica defensa contra un
    /// <c>exchangeRateQuoteId</c> inventado por un cliente HTTP es <c>[JsonIgnore]</c> — verificamos
    /// que System.Text.Json (el mismo serializador que usa ASP.NET Core para <c>[FromBody]</c>, con
    /// las opciones "Web" default: camelCase + case-insensitive) IGNORA la propiedad al deserializar,
    /// sea cual sea el valor que mande el JSON, y CUALQUIERA sea <c>isCreditNote</c>.
    /// </summary>
    [Fact]
    public void Adr011_IsCreditNoteConQuoteIdInventadoEnElJson_SeIgnoraAlDeserializar()
    {
        const string jsonConQuoteIdInventado = """
            {
              "reservaId": "11111111-1111-1111-1111-111111111111",
              "isCreditNote": true,
              "isDebitNote": false,
              "monId": "DOL",
              "monCotiz": 1234.56,
              "exchangeRateQuoteId": 999999,
              "exchangeRateFchCotiz": "2020-01-01"
            }
            """;

        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<CreateInvoiceRequest>(jsonConQuoteIdInventado, options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.IsCreditNote);
        Assert.Equal("DOL", deserialized.MonId); // el resto del payload SI deserializa normal.
        // [JsonIgnore]: el binder NUNCA pobla estas dos propiedades, sea cual sea el valor del JSON.
        Assert.Null(deserialized.ExchangeRateQuoteId);
        Assert.Null(deserialized.ExchangeRateFchCotiz);
    }

    // ============================================================
    // ADR-011 §8.2 (enmienda 2026-08-05): el SERVIDOR resuelve Source/QuoteId/FchCotiz comparando
    // el MonCotiz del request contra la sugerencia del resolver, por IGUALDAD EXACTA. Tests 16-21
    // de la spec.
    // ============================================================

    private static readonly ExchangeRateSuggestion SampleSuggestion = new(
        Rate: 1234.56m,
        RateDate: new DateOnly(2026, 08, 05),
        Source: ExchangeRateSource.AfipOficial,
        ProviderName: "ARCA_WSFEv1",
        ArcaFchCotiz: new DateOnly(2026, 08, 05),
        IsStale: false,
        QuoteId: 77,
        FetchedAt: new DateTime(2026, 08, 05, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>Test 16: MonCotiz EXACTAMENTE igual a la sugerencia -> AfipOficial + QuoteId, SIN justificacion.</summary>
    [Fact]
    public async Task Adr011_MonCotizIgualALaSugerencia_QuedaAfipOficial_SinJustificacionRequerida()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        _exchangeRateResolverMock
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleSuggestion);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = SampleSuggestion.Rate; // exactamente igual, byte a byte.
        request.ExchangeRateJustification = null; // el front NO manda justificacion cuando acepta la sugerencia.

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal(ExchangeRateSource.AfipOficial, captured[0].ExchangeRateSource);
        Assert.Equal(SampleSuggestion.QuoteId, captured[0].ExchangeRateQuoteId);
        Assert.Equal(SampleSuggestion.ArcaFchCotiz, captured[0].ExchangeRateFchCotiz);
        Assert.Equal(SampleSuggestion.FetchedAt, captured[0].ExchangeRateFetchedAt);
    }

    /// <summary>Test 17: MonCotiz distinto de la sugerencia (aunque sea por 0.000001) -> Manual + justificacion exigida.</summary>
    [Fact]
    public async Task Adr011_MonCotizDistintoDeLaSugerencia_QuedaManual_YExigeJustificacion()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        _exchangeRateResolverMock
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleSuggestion);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        // Distinto por la mas minima fraccion: el usuario piso el numero sugerido.
        request.MonCotiz = SampleSuggestion.Rate + 0.000001m;
        request.ExchangeRateJustification = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));
        Assert.Contains("justificacion", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(captured);

        // Con la justificacion puesta, ahora si pasa y queda Manual (no AfipOficial, aunque el
        // numero este a un centesimo de milesimo de distancia).
        request.ExchangeRateJustification = "El cliente pidio un TC distinto al sugerido.";
        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal(ExchangeRateSource.Manual, captured[0].ExchangeRateSource);
        Assert.Null(captured[0].ExchangeRateQuoteId);
    }

    /// <summary>Test 18: sin sugerencia disponible -> Manual + justificacion exigida.</summary>
    [Fact]
    public async Task Adr011_SinSugerenciaDisponible_QuedaManual_YExigeJustificacion()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);
        // El mock del ctor ya devuelve null por defecto (libreta vacia).

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;
        request.ExchangeRateJustification = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User 1", CancellationToken.None));
        Assert.Empty(captured);
    }

    /// <summary>Test 19: el request manda un ExchangeRateSource inventado -> el servidor lo ignora y pone el suyo.</summary>
    [Fact]
    public async Task Adr011_ElServidorIgnoraLaFuenteInventadaDelRequest()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        _exchangeRateResolverMock
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleSuggestion);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = SampleSuggestion.Rate;
        // Front "viejo" (pre-Deploy2) que sigue mandando una fuente inventada sin haber consultado nada.
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        var fechaInventadaPorElFrontViejo = DateTime.UtcNow.AddDays(-30);
        request.ExchangeRateFetchedAt = fechaInventadaPorElFrontViejo;

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        // El servidor puso AfipOficial (lo que devolvio el resolver), NO lo que mando el request.
        // OJO: captured[0] es el MISMO objeto que request (se pasa por referencia), asi que hay que
        // comparar contra el valor original guardado ANTES de la llamada, no contra request.* despues.
        Assert.Equal(ExchangeRateSource.AfipOficial, captured[0].ExchangeRateSource);
        Assert.NotEqual(fechaInventadaPorElFrontViejo, captured[0].ExchangeRateFetchedAt);
        Assert.Equal(SampleSuggestion.FetchedAt, captured[0].ExchangeRateFetchedAt);
    }

    /// <summary>Test 20: no-regresion en pesos — payload y MonCotiz byte-identicos a antes de esta obra.</summary>
    [Fact]
    public async Task Adr011_NoRegresionEnPesos_MonCotizQuedaByteIdenticoAlDefault()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest(); // MonId="PES" (default), MonCotiz=1m (default).

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal("PES", captured[0].MonId);
        Assert.Equal(1m, captured[0].MonCotiz);
        Assert.Null(captured[0].ExchangeRateSource);
        Assert.Null(captured[0].ExchangeRateQuoteId);
        // El resolver NUNCA se llama para pesos (corta antes, ni siquiera pregunta).
        _exchangeRateResolverMock.Verify(
            r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Test 21: la NC sigue copiando MonCotiz del original y heredando Source/QuoteId; NO llama al
    /// resolver (§6.2, "nunca se recotiza"). Igual que la NC/ND real (BookingCancellationService),
    /// el request ya llega con los campos heredados COMPLETOS antes de pasar por CreateAsync.
    /// </summary>
    [Fact]
    public async Task Adr011_NotaDeCredito_HeredaElTcDelOriginal_YNuncaLlamaAlResolver()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var request = BuildBaseRequest();
        request.IsCreditNote = true;
        // La NC necesita un OriginalInvoiceId para el pipeline real; CreatePendingInvoice esta
        // mockeado en este test asi que no hace falta que resuelva a una factura real en BD.
        request.OriginalInvoiceId = Guid.NewGuid().ToString();
        request.MonId = "DOL";
        request.MonCotiz = 1300m; // el TC CONGELADO del original, no una sugerencia de hoy.
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa; // heredado del original.
        request.ExchangeRateFetchedAt = new DateTime(2026, 07, 01, 12, 0, 0, DateTimeKind.Utc);
        request.ExchangeRateJustification = "Heredado de la factura original.";
        // ADR-011 §6.2 (fix detalle #4, revision post-implementacion 2026-08-05): la NC ahora
        // hereda TAMBIEN el puntero de procedencia (antes solo Source/FetchedAt/Justification).
        request.ExchangeRateQuoteId = 55;
        request.ExchangeRateFchCotiz = new DateOnly(2026, 07, 01);

        await service.CreateAsync(request, "u1", "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal(1300m, captured[0].MonCotiz);
        Assert.Equal(ExchangeRateSource.BNA_VendedorDivisa, captured[0].ExchangeRateSource);
        // QuoteId/FchCotiz sobreviven intactos: CreateAsync NO los limpia para IsCreditNote=true
        // (la limpieza incondicional del fix BLOQUEANTE F-4 es SOLO para venta genuina).
        Assert.Equal(55, captured[0].ExchangeRateQuoteId);
        Assert.Equal(new DateOnly(2026, 07, 01), captured[0].ExchangeRateFchCotiz);
        _exchangeRateResolverMock.Verify(
            r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // Tests de AfipService.CreatePendingInvoice: poblado real de columnas (b) + A/B/C (e).
    // ============================================================

    private static AfipService BuildAfipService(AppDbContext context)
        => new(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            new NoopProtector());

    // Protector inerte: no encripta nada. AfipService no usa certificados en estos tests
    // (CreatePendingInvoice solo arma la Invoice PENDING, no llama a ARCA).
    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }

    /// <summary>
    /// Siembra AFIP settings + un cliente con la condicion fiscal pedida + reserva con ese
    /// cliente como Payer. Devuelve nada: el test lee de context tras CreatePendingInvoice.
    /// </summary>
    private static async Task SeedAfipScenarioAsync(
        AppDbContext context,
        string agencyTaxCondition,
        string customerTaxCondition)
    {
        context.AfipSettings.Add(new AfipSettings
        {
            Id = 1,
            PuntoDeVenta = 7,
            TaxCondition = agencyTaxCondition
        });

        var customer = new Customer
        {
            Id = 10,
            FullName = "Cliente Test",
            TaxCondition = customerTaxCondition
        };
        context.Customers.Add(customer);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-ADR012-AFIP",
            Name = "Reserva AFIP",
            Status = EstadoReserva.Confirmed,
            TotalSale = 100m,
            Balance = 0m,
            TotalPaid = 100m,
            PayerId = 10,
            Payer = customer
        });

        await context.SaveChangesAsync();
    }

    private static CreateInvoiceRequest BuildUsdRequest() => new()
    {
        ReservaId = "1",
        IsCreditNote = false,
        IsDebitNote = false,
        MonId = "DOL",
        MonCotiz = 1234.56m,
        ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa,
        ExchangeRateFetchedAt = DateTime.UtcNow,
        ExchangeRateJustification = "TC vendedor divisa BNA (RG 5616).",
        Items = new List<InvoiceItemDto>
        {
            new() { Description = "Paquete", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 }
        }
    };

    [Fact]
    public async Task CreatePendingInvoice_WithUsd_PopulatesExchangeRateTraceColumns()
    {
        using var context = new AppDbContext(_dbOptions);
        // Monotributo -> C, pero la moneda no influye en el tipo (lo verificamos abajo).
        await SeedAfipScenarioAsync(context, agencyTaxCondition: "Monotributo", customerTaxCondition: "Consumidor Final");

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, BuildUsdRequest());

        var persisted = await context.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(persisted);
        Assert.Equal("DOL", persisted!.MonId);
        Assert.Equal(1234.56m, persisted.MonCotiz);
        Assert.Equal(ExchangeRateSource.BNA_VendedorDivisa, persisted.ExchangeRateSource);
        Assert.NotNull(persisted.ExchangeRateFetchedAt);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ExchangeRateJustification));
    }

    [Theory]
    // Agencia RI + cliente RI -> A (1). Agencia RI + cliente Monotributo -> A (1) (Ley 27.618).
    // Agencia RI + cliente Consumidor Final -> B (6). Agencia Monotributo -> C (11).
    // La moneda (USD) NO debe cambiar estos resultados.
    [InlineData("Responsable Inscripto", "Responsable Inscripto", 1)]
    [InlineData("Responsable Inscripto", "Monotributo", 1)]        // FIX fiscal 2026-06-13: antes daba B
    [InlineData("Responsable Inscripto", "Consumidor Final", 6)]
    [InlineData("Monotributo", "Consumidor Final", 11)]
    [InlineData("Exento", "Consumidor Final", 11)]
    public async Task CreatePendingInvoice_WithUsd_KeepsAbcDecisionUnchanged(
        string agencyTaxCondition,
        string customerTaxCondition,
        int expectedTipoComprobante)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedAfipScenarioAsync(context, agencyTaxCondition, customerTaxCondition);

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, BuildUsdRequest());

        // El tipo de comprobante depende SOLO de la condicion fiscal, no de la moneda.
        Assert.Equal(expectedTipoComprobante, invoice.TipoComprobante);
        Assert.Equal("DOL", invoice.MonId);
    }

    // ============================================================
    // Fix fiscal RI->Monotributista (2026-06-13): leyenda obligatoria Ley 27.618.
    // ============================================================

    /// <summary>
    /// Emisor RI a receptor Monotributo: la Factura A debe persistir la leyenda obligatoria de
    /// la Ley 27.618 en Invoice.FiscalLegend (el job la mandara a ARCA en el campo Obs).
    /// </summary>
    [Fact]
    public async Task CreatePendingInvoice_RI_a_Monotributo_PersistsLey27618Legend()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedAfipScenarioAsync(context, agencyTaxCondition: "Responsable Inscripto", customerTaxCondition: "Monotributo");

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, BuildUsdRequest());

        // Factura A.
        Assert.Equal(1, invoice.TipoComprobante);
        var persisted = await context.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(persisted);
        Assert.Equal(InvoiceTypeResolver.LeyendaFacturaAMonotributista, persisted!.FiscalLegend);
    }

    /// <summary>
    /// La variante de texto del receptor ("MONOTRIBUTISTA") no degrada: sigue dando Factura A y
    /// la leyenda igual se persiste (no se pierde por el formato del dato).
    /// </summary>
    [Fact]
    public async Task CreatePendingInvoice_RI_a_MonotributoVariante_StillFacturaA_WithLegend()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedAfipScenarioAsync(context, agencyTaxCondition: "Responsable Inscripto", customerTaxCondition: "MONOTRIBUTISTA");

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, BuildUsdRequest());

        Assert.Equal(1, invoice.TipoComprobante);
        var persisted = await context.Invoices.FindAsync(invoice.Id);
        Assert.Equal(InvoiceTypeResolver.LeyendaFacturaAMonotributista, persisted!.FiscalLegend);
    }

    /// <summary>
    /// La leyenda va SOLO en RI->Monotributo. En los demas casos (RI->RI, RI->CF, Mono, Exento)
    /// FiscalLegend queda NULL -> el job NO emite el nodo Obs (envelope byte-identico al historico).
    /// </summary>
    [Theory]
    [InlineData("Responsable Inscripto", "Responsable Inscripto")] // Factura A pero RI->RI: sin leyenda
    [InlineData("Responsable Inscripto", "Consumidor Final")]      // Factura B: sin leyenda
    [InlineData("Responsable Inscripto", "Exento")]                // Factura B (RI->Exento): sin leyenda
    [InlineData("Responsable Inscripto", "Extranjero")]            // Factura B (RI->Extranjero): sin leyenda
    [InlineData("Monotributo", "Monotributo")]                     // Factura C: sin leyenda
    [InlineData("Exento", "Consumidor Final")]                     // Factura C: sin leyenda
    public async Task CreatePendingInvoice_OtherCases_DoNotPersistLegend(
        string agencyTaxCondition,
        string customerTaxCondition)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedAfipScenarioAsync(context, agencyTaxCondition, customerTaxCondition);

        var afip = BuildAfipService(context);
        var invoice = await afip.CreatePendingInvoice(1, BuildUsdRequest());

        var persisted = await context.Invoices.FindAsync(invoice.Id);
        Assert.Null(persisted!.FiscalLegend);
    }
}
