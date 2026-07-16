using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda B (2026-07-16): unifica el snapshot fiscal server-side en anulaciones. Antes de esta tanda,
/// el camino de anulacion TOTAL (<see cref="BookingCancellationService.ConfirmAsync"/>) tomaba las 3
/// condiciones fiscales (agencia/operador/cliente) y la moneda/TC de <c>request.SnapshotData</c> —
/// un payload que arma el FRONTEND con datos hardcodeados/adivinados (penaltyPayload.js mandaba
/// "Responsable Inscripto" fijo para el operador, "Consumidor Final" fijo para el cliente, y
/// ARS/1.0 SIEMPRE, sin importar la moneda real de la factura). Esta clase testea:
///
/// <list type="bullet">
/// <item><see cref="BookingCancellationService.ResolveServerSideTaxIdentity"/> como funcion pura
/// (sin DB): el helper compartido que ahora usan TANTO el camino total COMO el camino T5.</item>
/// <item><c>ConfirmAsync</c> extremo a extremo (InMemory): que ignora <c>request.SnapshotData</c>
/// por completo (test "guardian" con un payload envenenado), que deriva moneda/TC/Source de la
/// factura original, que puebla los 2 CUIT, y los 3 modos de falla nuevos (INV-118 por ficha
/// incompleta, INV-118 por factura extranjera sin fuente de TC, INV-120 por TC manual sin
/// justificacion).</item>
/// </list>
///
/// <para><b>Trade-off (igual que el resto del modulo)</b>: EF InMemory no valida CHECK SQL ni xmin;
/// la atomicidad real se valida en integracion Postgres. Aca se cubre la LOGICA de derivacion.</para>
/// </summary>
public class TandaBServerSideFiscalSnapshotTests
{
    // ============================================================
    // Parte 1 — ResolveServerSideTaxIdentity: funcion pura, sin DB.
    // ============================================================

    [Fact]
    public void ResolveServerSideTaxIdentity_HappyPath_NormalizesConditions_AndTrimsTaxIds()
    {
        var afipSettings = new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111112 };
        var supplier = new Supplier { Name = "Turismo Andina", TaxCondition = "IVA_RESP_INSCRIPTO", TaxId = "  30-71159849-8  " };
        var customer = new Customer { FullName = "Cliente", TaxCondition = "Consumidor Final", TaxId = "20-33445566-7" };

        var identity = BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings, supplier, customer);

        Assert.Equal("MONOTRIBUTISTA", identity.AgencyTaxCondition);
        Assert.Equal("RESPONSABLE_INSCRIPTO", identity.SupplierTaxCondition);
        Assert.Equal("CONSUMIDOR_FINAL", identity.CustomerTaxCondition);
        // El CUIT del operador llega con espacios (typo de carga tipico): el helper lo recorta.
        Assert.Equal("30-71159849-8", identity.SupplierTaxId);
        Assert.Equal("20-33445566-7", identity.CustomerTaxId);
    }

    [Fact]
    public void ResolveServerSideTaxIdentity_AgencyUnknown_ThrowsInv118()
    {
        // Sin fila de AfipSettings (agencia null) = condicion Unknown: la agencia NUNCA tuvo su
        // ficha fiscal completada.
        var supplier = new Supplier { Name = "Operador", TaxCondition = "IVA_RESP_INSCRIPTO" };
        var customer = new Customer { FullName = "Cliente", TaxCondition = "Consumidor Final" };

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings: null, supplier, customer));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Contains("ficha de la agencia", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveServerSideTaxIdentity_SupplierUnknown_ThrowsInv118_NamesSupplierByCommercialName()
    {
        var afipSettings = new AfipSettings { TaxCondition = "Monotributo" };
        var supplier = new Supplier { Name = "Turismo Andina", TaxCondition = null }; // ficha incompleta
        var customer = new Customer { FullName = "Cliente", TaxCondition = "Consumidor Final" };

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings, supplier, customer));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Contains("ficha del operador Turismo Andina", ex.Message);
    }

    [Fact]
    public void ResolveServerSideTaxIdentity_CustomerUnknown_ThrowsInv118()
    {
        var afipSettings = new AfipSettings { TaxCondition = "Monotributo" };
        var supplier = new Supplier { Name = "Operador", TaxCondition = "IVA_RESP_INSCRIPTO" };
        var customer = new Customer { FullName = "Cliente", TaxCondition = "algo-que-no-existe" }; // normaliza a Unknown

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings, supplier, customer));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Contains("ficha del cliente", ex.Message);
    }

    [Fact]
    public void ResolveServerSideTaxIdentity_WhitespaceTaxId_NormalizedToNull()
    {
        var afipSettings = new AfipSettings { TaxCondition = "Monotributo" };
        var supplier = new Supplier { Name = "Operador", TaxCondition = "IVA_RESP_INSCRIPTO", TaxId = "   " };
        var customer = new Customer { FullName = "Cliente", TaxCondition = "Consumidor Final", TaxId = "" };

        var identity = BookingCancellationService.ResolveServerSideTaxIdentity(afipSettings, supplier, customer);

        // Un CUIT cargado como espacios/vacio NO es un dato: no se persiste como basura.
        Assert.Null(identity.SupplierTaxId);
        Assert.Null(identity.CustomerTaxId);
    }

    // ============================================================
    // Parte 2 — ConfirmAsync extremo a extremo (InMemory).
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"tandab-confirm-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildService(AppDbContext ctx) =>
        new(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            BuildSettingsService(),
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

    private static IOperationalFinanceSettingsService BuildSettingsService()
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnablePartialCreditNotes = false,
                EnableCancellationDebitNote = false,
                OperatorRefundTimeoutDays = 60,
            });
        return settingsMock.Object;
    }

    /// <summary>
    /// Siembra el esqueleto minimo para confirmar una anulacion TOTAL: agencia (AfipSettings,
    /// opcional), operador, cliente, reserva con un hotel pagado, factura original y el BC ya en
    /// Drafted con su linea (mismo molde que el resto del modulo — construccion DIRECTA del BC en
    /// vez de pasar por DraftAsync, para poder controlar cada campo fiscal a mano).
    /// </summary>
    private static async Task<(BookingCancellation Bc, Supplier Supplier, Customer Customer, Invoice Invoice)>
        SeedConfirmableAsync(
            AppDbContext ctx,
            bool seedAgency = true,
            string agencyTaxCondition = "Monotributo",
            string? supplierTaxCondition = "IVA_RESP_INSCRIPTO",
            string customerTaxCondition = "Consumidor Final",
            string? supplierTaxId = null,
            string? customerTaxId = null,
            string invoiceMonId = "PES",
            decimal invoiceMonCotiz = 1m,
            ExchangeRateSource? exchangeRateSource = null,
            string? exchangeRateJustification = null)
    {
        if (seedAgency)
        {
            ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = agencyTaxCondition });
        }

        var customer = new Customer
        {
            FullName = "Cliente Tanda B", IsActive = true,
            TaxCondition = customerTaxCondition, TaxId = customerTaxId,
        };
        var supplier = new Supplier
        {
            Name = "Operador Tanda B", IsActive = true,
            TaxCondition = supplierTaxCondition, TaxId = supplierTaxId,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"R-TB-{Guid.NewGuid():N}"[..12], Name = "Reserva Tanda B",
            PayerId = customer.Id, Status = EstadoReserva.Confirmed, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        string serviceCurrency = string.Equals(invoiceMonId, "PES", StringComparison.OrdinalIgnoreCase) ? "ARS" : "USD";
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 40_000m, SalePrice = 60_000m, Currency = serviceCurrency,
        });
        // Pago al operador (mismo patron que el resto del modulo): evita que el auto-cierre por
        // receivable $0 interfiera con lo que este test quiere observar (el FiscalSnapshot).
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 40_000m,
            Currency = serviceCurrency, Method = "Transferencia",
        });
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = Random.Shared.Next(1, 999_999),
            CAE = "cae-tandab", Resultado = "A",
            MonId = invoiceMonId, MonCotiz = invoiceMonCotiz,
            ImporteTotal = 60_000m, ImporteNeto = 60_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
            ExchangeRateSource = exchangeRateSource, ExchangeRateJustification = exchangeRateJustification,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Cliente decidio anular el viaje completo", DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 0,
            Scope = BookingCancellationLineScope.Full, Currency = serviceCurrency,
            LineSaleAmount = 60_000m, RefundCap = 40_000m, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        return (bc, supplier, customer, invoice);
    }

    /// <summary>
    /// Request de confirmacion con un <c>SnapshotData</c> ENVENENADO: dice cosas que NO coinciden con
    /// las fichas reales (agencia RI cuando en realidad es Monotributo, operador Exento cuando en
    /// realidad es RI, cliente RI cuando en realidad es Consumidor Final) ni con la factura real (EUR
    /// a una cotizacion inventada). Si <c>ConfirmAsync</c> todavia leyera este campo, el snapshot
    /// persistido reflejaria esta mentira. Sirve de guardian anti-reintroduccion del bug.
    /// </summary>
    private static ConfirmCancellationRequest PoisonedConfirmRequest() =>
        new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "EUR",
                ExchangeRateAtOriginalInvoice: 999m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "mentira armada por el frontend",
                AgencyTaxConditionAtEvent: "RESPONSABLE_INSCRIPTO",
                SupplierTaxConditionAtEvent: "EXENTO",
                CustomerTaxConditionAtEvent: "RESPONSABLE_INSCRIPTO"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    private static ConfirmCancellationRequest NoSnapshotConfirmRequest() =>
        new(
            SnapshotData: null,
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    [Fact]
    public async Task ConfirmAsync_PoisonedSnapshotData_IsIgnored_RealFichasWin()
    {
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx); // fichas reales: Monotributo/RI/ConsumidorFinal, factura PES.
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, PoisonedConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        // Las 3 condiciones son las REALES (de la base), no las mentirosas del request.
        Assert.Equal("MONOTRIBUTISTA", reloaded.FiscalSnapshot!.AgencyTaxConditionAtEvent);
        Assert.Equal("RESPONSABLE_INSCRIPTO", reloaded.FiscalSnapshot.SupplierTaxConditionAtEvent);
        Assert.Equal("CONSUMIDOR_FINAL", reloaded.FiscalSnapshot.CustomerTaxConditionAtEvent);
        // La moneda/TC son los de la FACTURA real (pesos), no "EUR"/999 del request.
        Assert.Equal("ARS", reloaded.FiscalSnapshot.CurrencyAtEvent);
        Assert.Equal(1m, reloaded.FiscalSnapshot.ExchangeRateAtOriginalInvoice);
        Assert.NotEqual(ExchangeRateSource.Manual, reloaded.FiscalSnapshot.Source);
    }

    [Fact]
    public async Task ConfirmAsync_WithoutSnapshotData_StillWorks_FieldIsIgnoredNotRequired()
    {
        // Front viejo en cache que ya no manda snapshotData: el campo es opcional desde Tanda B y el
        // service ni lo mira, asi que confirmar sigue funcionando igual.
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx);
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, reloaded.Status);
        Assert.Equal("ARS", reloaded.FiscalSnapshot!.CurrencyAtEvent);
    }

    [Fact]
    public async Task ConfirmAsync_ArsInvoice_MatchesPreviousBehavior_ParityCheck()
    {
        // Caso "bien declarado": factura en pesos, todas las fichas completas. La liquidacion debe
        // salir IDENTICA a como salia antes de Tanda B (cuando el front mandaba exactamente esto).
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx);
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal("ARS", reloaded.FiscalSnapshot!.CurrencyAtEvent);
        Assert.Equal(1m, reloaded.FiscalSnapshot.ExchangeRateAtOriginalInvoice);
        Assert.Equal(ExchangeRateSource.BNA_Minorista, reloaded.FiscalSnapshot.Source);
        Assert.Null(reloaded.FiscalSnapshot.ManualJustification);
    }

    [Fact]
    public async Task ConfirmAsync_ForeignInvoice_WithReliableSource_UsesInvoiceCurrencyRateAndSource()
    {
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(
            ctx, invoiceMonId: "DOL", invoiceMonCotiz: 950m,
            exchangeRateSource: ExchangeRateSource.BNA_VendedorDivisa);
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal("USD", reloaded.FiscalSnapshot!.CurrencyAtEvent); // ISO, no el codigo ARCA "DOL".
        Assert.Equal(950m, reloaded.FiscalSnapshot.ExchangeRateAtOriginalInvoice);
        Assert.Equal(ExchangeRateSource.BNA_VendedorDivisa, reloaded.FiscalSnapshot.Source);
    }

    [Fact]
    public async Task ConfirmAsync_ForeignInvoice_WithoutExchangeRateSource_ThrowsInv118()
    {
        // ANTES de Tanda B esto quedaba ENMASCARADO: el request siempre mandaba ARS/1.0 y la NC
        // total salia mal cotizada en silencio. Ahora se corta ANTES de emitir nada.
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(
            ctx, invoiceMonId: "DOL", invoiceMonCotiz: 950m, exchangeRateSource: null);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(
                bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
                requesterIsAdmin: false, ct: default));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Equal(
            "Esta cancelación necesita que resuelvas el tipo de cambio de la factura original.",
            ex.Message);
    }

    [Fact]
    public async Task ConfirmAsync_ForeignInvoice_ManualSourceWithoutJustification_ThrowsInv120()
    {
        // M3 del addendum: defensa en profundidad para facturas historicas que pudieron quedar con
        // Source=Manual sin justificacion (creadas antes del guard de InvoiceService, o con el flag
        // multimoneda apagado en su momento).
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(
            ctx, invoiceMonId: "DOL", invoiceMonCotiz: 950m,
            exchangeRateSource: ExchangeRateSource.Manual, exchangeRateJustification: null);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(
                bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
                requesterIsAdmin: false, ct: default));

        Assert.Equal("INV-120", ex.InvariantCode);
    }

    [Fact]
    public async Task ConfirmAsync_ForeignInvoice_ManualSourceWithJustification_Succeeds()
    {
        // Contraparte del test anterior: el guard M3 NO debe sobre-disparar cuando la justificacion
        // SI esta cargada.
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(
            ctx, invoiceMonId: "DOL", invoiceMonCotiz: 950m,
            exchangeRateSource: ExchangeRateSource.Manual,
            exchangeRateJustification: "Cliente trajo el comprobante del banco con la cotizacion del dia.");
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal("USD", reloaded.FiscalSnapshot!.CurrencyAtEvent);
        Assert.Equal(ExchangeRateSource.Manual, reloaded.FiscalSnapshot.Source);
        Assert.Equal(
            "Cliente trajo el comprobante del banco con la cotizacion del dia.",
            reloaded.FiscalSnapshot.ManualJustification);
    }

    [Fact]
    public async Task ConfirmAsync_SupplierWithoutTaxCondition_ThrowsInv118_MentionsSupplierFicha()
    {
        // Caso realista: un operador cargado a las apuradas, sin la condicion fiscal completada.
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx, supplierTaxCondition: null);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(
                bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
                requesterIsAdmin: false, ct: default));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Contains("ficha del operador", ex.Message);
        // Gate estricto de exposicion de internos: nada de jerga tecnica en el mensaje al usuario.
        Assert.DoesNotContain("TaxCondition", ex.Message);
        Assert.DoesNotContain("Unknown", ex.Message);
    }

    [Fact]
    public async Task ConfirmAsync_NoAfipSettingsRow_ThrowsInv118_MentionsAgencyFicha()
    {
        // Instalacion nueva sin AfipSettings cargado todavia: la agencia tambien es una ficha que se
        // puede completar (Configuracion > Facturacion), no un caso especial.
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx, seedAgency: false);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(
                bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
                requesterIsAdmin: false, ct: default));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Contains("ficha de la agencia", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAsync_PopulatesSupplierAndCustomerCuit_FromRealFichas()
    {
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(
            ctx, supplierTaxId: "30-71159849-8", customerTaxId: "20-33445566-7");
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        // Declarados en FiscalSnapshot desde FC1, pero NADIE los poblaba hasta esta tanda.
        Assert.Equal("30-71159849-8", reloaded.FiscalSnapshot!.SupplierTaxIdAtEvent);
        Assert.Equal("20-33445566-7", reloaded.FiscalSnapshot.CustomerTaxIdAtEvent);
    }

    [Fact]
    public async Task ConfirmAsync_NoCuitLoaded_PersistsNull_NotEmptyString()
    {
        using var ctx = NewDbContext();
        var (bc, _, _, _) = await SeedConfirmableAsync(ctx); // supplierTaxId/customerTaxId quedan null por default.
        var service = BuildService(ctx);

        await service.ConfirmAsync(
            bc.PublicId, NoSnapshotConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.FiscalSnapshot!.SupplierTaxIdAtEvent);
        Assert.Null(reloaded.FiscalSnapshot.CustomerTaxIdAtEvent);
    }
}
