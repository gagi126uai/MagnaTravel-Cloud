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

    public MultiCurrencyInvoicingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
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
        out List<CreateInvoiceRequest> capturedRequests)
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
            httpContextAccessor: null);
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
    // (b) Flag ON + USD con TC valido + fuente + fecha + justificacion -> pasa y forwardea.
    //     (El poblado real de columnas se verifica en el test de AfipService de abajo.)
    // ============================================================

    [Fact]
    public async Task FlagOn_WithValidForeignCurrency_PassesAndForwardsTraceFields()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

        var fetchedAt = DateTime.UtcNow;
        var request = BuildBaseRequest();
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;
        request.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
        request.ExchangeRateFetchedAt = fetchedAt;
        request.ExchangeRateJustification = "TC vendedor divisa BNA dia habil anterior (RG 5616).";

        await service.CreateAsync(request, userId: "u1", userName: "User 1", CancellationToken.None);

        Assert.Single(captured);
        Assert.Equal("DOL", captured[0].MonId);
        Assert.Equal(1234.56m, captured[0].MonCotiz);
        Assert.Equal(ExchangeRateSource.BNA_VendedorDivisa, captured[0].ExchangeRateSource);
        Assert.Equal(fetchedAt, captured[0].ExchangeRateFetchedAt);
        Assert.False(string.IsNullOrWhiteSpace(captured[0].ExchangeRateJustification));
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
        using var context = new AppDbContext(_dbOptions);
        await SeedSettledReservaAsync(context);

        var service = BuildInvoiceService(context, enableMultiCurrency: true, out var captured);

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
    // Agencia RI + cliente RI -> A (1). Agencia RI + cliente no RI -> B (6).
    // Agencia Monotributo -> C (11). La moneda (USD) NO debe cambiar estos resultados.
    [InlineData("Responsable Inscripto", "Responsable Inscripto", 1)]
    [InlineData("Responsable Inscripto", "Consumidor Final", 6)]
    [InlineData("Monotributo", "Consumidor Final", 11)]
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
}
