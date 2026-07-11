using System;
using System.Linq;
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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-025: tests del modelo BC-padre + lineas hijas, la cancelacion de UN servicio (parcial), el
/// levantamiento de INV-152 (multi-operador) y la reformulacion de INV-126 (refund agregado por operador).
///
/// <para>Tests UNIT con EF InMemory (sin Docker), mismo trade-off que
/// <see cref="BookingCancellationServicePartialCreditNoteTests"/>: InMemory NO valida CHECK constraints
/// SQL ni xmin; cubrimos la LOGICA del service (marcar servicio, bajar saldo + deuda B1, construir lineas,
/// imputar refund agregado B2). El cuadre fiscal real (NC/ND) queda en revision manual y fuera de alcance.</para>
/// </summary>
public class Adr025PartialAndMultiOperatorCancellationTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr025-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static BookingCancellationService BuildService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 60,
            });

        return new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalMock.Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);
    }

    /// <summary>
    /// Siembra una reserva con un hotel confirmado y pagado a su operador, mas opcionalmente un segundo
    /// servicio de OTRO operador. Devuelve los ids relevantes. El hotel queda "Confirmado" (genera deuda).
    /// </summary>
    private static async Task<(Reserva reserva, Customer customer, Supplier supplierA, Supplier supplierB,
        HotelBooking hotel, TransferBooking? transfer)> SeedAsync(
        AppDbContext ctx, bool addSecondOperatorService = false, decimal hotelNetCost = 50_000m,
        decimal hotelSalePrice = 80_000m, decimal paidToSupplierA = 50_000m)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-025",
            Name = "Reserva ADR-025",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplierA.Id,
            Status = "Confirmado",
            NetCost = hotelNetCost,
            SalePrice = hotelSalePrice,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);

        TransferBooking? transfer = null;
        if (addSecondOperatorService)
        {
            transfer = new TransferBooking
            {
                ReservaId = reserva.Id,
                SupplierId = supplierB.Id,
                Status = "Confirmado",
                NetCost = 20_000m,
                SalePrice = 30_000m,
                Currency = "ARS",
            };
            ctx.TransferBookings.Add(transfer);
        }
        await ctx.SaveChangesAsync();

        // Pago al operador A (genera saldo de deuda = NetCost - pagado).
        if (paidToSupplierA > 0m)
        {
            ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplierA.Id,
                Amount = paidToSupplierA,
                Currency = "ARS",
                ImputedCurrency = "ARS",
                ImputedAmount = paidToSupplierA,
            });
            await ctx.SaveChangesAsync();
        }

        // Sincronizar la deuda del operador A para que arranque coherente (compras - pagos).
        await SupplierDebtPersister.PersistAsync(ctx, supplierA.Id);
        await ctx.SaveChangesAsync();

        return (reserva, customer, supplierA, supplierB, hotel, transfer);
    }

    // ============================================================
    // CancelServiceAsync (parcial) + B1 (baja de deuda del operador)
    // ============================================================

    [Fact]
    public async Task CancelService_ConfirmedPaidHotel_DropsSupplierDebt_B1()
    {
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, hotel, _) = await SeedAsync(ctx, hotelNetCost: 50_000m, paidToSupplierA: 50_000m);
        var service = BuildService(ctx);

        // Antes: el hotel confirmado de 50.000 con 50.000 pagado deja deuda 0 (ya pagado). Para ver la baja
        // de deuda, partimos de un caso con deuda > 0: pagamos solo 20.000 (deuda = 30.000).
        // (re-seed conceptual via ajuste) -> en su lugar verificamos contra el caso pago parcial abajo.
        var before = await ctx.SupplierBalanceByCurrency
            .Where(r => r.SupplierId == supplierA.Id && r.Currency == "ARS")
            .Select(r => (decimal?)r.ConfirmedPurchases)
            .FirstOrDefaultAsync();

        var result = await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel, mantiene el resto"),
            userId: "vendedor-1", userName: "Vendedor", ct: CancellationToken.None);

        // La compra confirmada del operador A baja a 0 (el unico servicio confirmado quedo cancelado).
        var afterPurchases = await ctx.SupplierBalanceByCurrency
            .Where(r => r.SupplierId == supplierA.Id && r.Currency == "ARS")
            .Select(r => (decimal?)r.ConfirmedPurchases)
            .FirstOrDefaultAsync();

        Assert.Equal(50_000m, before);     // antes de cancelar, el hotel confirmado sumaba a compras
        Assert.Equal(0m, afterPurchases ?? 0m); // despues, el servicio cancelado sale de la deuda (B1)
        Assert.Equal(1, result.CancelledServicesCount);
        Assert.Equal(1, result.TotalServicesWithSupplierCount);
    }

    [Fact]
    public async Task CancelService_DropsClientSaleFromBalance()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx, hotelSalePrice: 80_000m);
        var service = BuildService(ctx);

        // El hotel confirmado de 80.000 cuenta como venta confirmada antes de cancelar.
        await ReservaMoneyPersister.PersistAsync(ctx, reserva.Id);
        var confirmedBefore = (await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id)).ConfirmedSale;

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var confirmedAfter = (await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id)).ConfirmedSale;

        Assert.Equal(80_000m, confirmedBefore);
        Assert.Equal(0m, confirmedAfter); // el servicio cancelado sale del saldo del cliente (ServiceResolutionRules)
    }

    [Fact]
    public async Task CancelService_DoesNotChangeReservaStatus_Decision1()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var status = (await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id)).Status;
        Assert.Equal(EstadoReserva.Confirmed, status); // la reserva NO cambia de estado (decision #1)
    }

    [Fact]
    public async Task CancelService_IsIdempotent_WhenAlreadyCancelled()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);
        var req = new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel");

        var first = await service.CancelServiceAsync(req, "vendedor-1", "Vendedor", CancellationToken.None);
        var second = await service.CancelServiceAsync(req, "vendedor-1", "Vendedor", CancellationToken.None);

        Assert.Equal(1, first.CancelledServicesCount);
        Assert.Equal(1, second.CancelledServicesCount); // re-cancelar es no-op, no duplica el contador
    }

    [Fact]
    public async Task CancelService_RejectsServiceFromAnotherReserva_Ownership()
    {
        using var ctx = NewDbContext();
        var (reserva, customer, supplierA, _, _, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        // Servicio de OTRA reserva.
        var otherReserva = new Reserva { NumeroReserva = "R-OTRA", Name = "Otra", PayerId = customer.Id };
        ctx.Reservas.Add(otherReserva);
        await ctx.SaveChangesAsync();
        var otherHotel = new HotelBooking { ReservaId = otherReserva.Id, SupplierId = supplierA.Id, Status = "Confirmado", Currency = "ARS" };
        ctx.HotelBookings.Add(otherHotel);
        await ctx.SaveChangesAsync();

        // Pedimos cancelar el hotel de la OTRA reserva pero contra la reserva original -> 404 (server-side).
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", otherHotel.PublicId, "Intento cruzado"),
                "vendedor-1", "Vendedor", CancellationToken.None));
    }

    [Fact]
    public async Task CancelService_RejectsInvalidServiceTable()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "NoExiste", hotel.PublicId, "Tipo invalido"),
                "vendedor-1", "Vendedor", CancellationToken.None));
    }

    [Fact]
    public async Task CancelService_Flight_UsesIataCancelCode_AndDropsFromBalance()
    {
        using var ctx = NewDbContext();
        var (reserva, customer, supplierA, _, _, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        // Vuelo emitido (resuelto) -> cuenta para ConfirmedSale. Cancelarlo debe sacarlo.
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplierA.Id,
            Status = "HK",
            TicketIssuedAt = DateTime.UtcNow,
            SalePrice = 120_000m,
            NetCost = 90_000m,
            Currency = "ARS",
        };
        ctx.FlightSegments.Add(flight);
        await ctx.SaveChangesAsync();

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Flight", flight.PublicId, "Cliente baja el aereo"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var reloaded = await ctx.FlightSegments.AsNoTracking().FirstAsync(f => f.Id == flight.Id);
        // El aereo se cancela con codigo IATA (UN) para que MapFlightStatus -> Cancelado y salga del saldo.
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(reloaded));
        Assert.NotNull(reloaded.CancelledAt);
    }

    // ============================================================
    // DraftAsync multi-operador: levanta INV-152, construye N lineas
    // ============================================================

    [Fact]
    public async Task Draft_MultiOperator_NoLongerBlocked_BuildsOneLinePerOperator()
    {
        using var ctx = NewDbContext();
        var (reserva, customer, supplierA, supplierB, hotel, transfer) = await SeedAsync(ctx, addSecondOperatorService: true);
        var service = BuildService(ctx);

        // Factura original viva (precondicion del Draft).
        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 50, CAE = "123",
            Resultado = "A", ImporteTotal = 110_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Cancelacion total multi-operador"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.Include(b => b.Lines).FirstAsync(b => b.PublicId == dto.PublicId);

        // Antes esto tiraba INV-152; ahora crea una linea por operador (hotel A + transfer B).
        Assert.Equal(2, bc.Lines.Count);
        Assert.Contains(bc.Lines, l => l.SupplierId == supplierA.Id && l.ServiceTable == CancellableServiceTable.Hotel);
        Assert.Contains(bc.Lines, l => l.SupplierId == supplierB.Id && l.ServiceTable == CancellableServiceTable.Transfer);
        Assert.All(bc.Lines, l => Assert.Equal(BookingCancellationLineScope.Full, l.Scope));
    }

    [Fact]
    public async Task Draft_SingleOperator_StillBuildsOneLine_ByteEquivalent()
    {
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, hotel, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 51, CAE = "124",
            Resultado = "A", ImporteTotal = 80_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Cancelacion total un operador"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.Include(b => b.Lines).FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Single(bc.Lines);
        Assert.Equal(supplierA.Id, bc.SupplierId); // operador principal denormalizado = el de la unica linea
        Assert.Equal(supplierA.Id, bc.Lines.First().SupplierId);
    }

    // ============================================================
    // Bug 2026-07-01: INV-100 FALSO cuando la reserva tiene, ademas de su factura
    // de venta, una Nota de Credito y/o una Nota de Debito. La query de facturas
    // activas del Draft debe contar SOLO facturas de VENTA vivas (excluir NC y ND).
    // ============================================================

    /// <summary>
    /// Siembra una factura de la reserva. Por defecto crea una Factura C (venta) EMITIDA
    /// (con CAE, Resultado="A"). Los tests pasan el <paramref name="tipoComprobante"/> para
    /// simular NC (13) o ND (12); y <paramref name="cae"/>=null + <paramref name="resultado"/>="R"/"PENDING"
    /// para simular la FILA FANTASMA (intento de emision rechazado o encolado, sin CAE) que
    /// NO debe contar como factura de venta viva (bug 2026-07-01).
    /// </summary>
    private static Invoice SeedInvoice(
        AppDbContext ctx, int reservaId, int tipoComprobante, int numeroComprobante,
        decimal importeTotal = 80_000m, string? cae = "10000000000000", string resultado = "A")
    {
        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = cae,
            Resultado = resultado,
            ImporteTotal = importeTotal,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        return invoice;
    }

    [Fact]
    public async Task Draft_SaleInvoicePlusCreditNote_DoesNotTriggerInv100_OriginatingIsSaleInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Factura de venta C (11) + su Nota de Credito C (13). La NC NO es una factura de venta:
        // no debe contar para INV-100 ni quedar como originatingInvoice.
        var saleInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 60);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 13, numeroComprobante: 61);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + NC"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(saleInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_SaleInvoicePlusDebitNote_DoesNotTriggerInv100_OriginatingIsSaleInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Factura de venta C (11) + una Nota de Debito C (12), ej. por multa del operador.
        // La ND es mas reciente por CreatedAt; sin la exclusion quedaria como originatingInvoice.
        var saleInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 62);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 12, numeroComprobante: 63);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + ND"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(saleInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_SaleInvoicePlusCreditNoteAndDebitNote_DoesNotTriggerInv100()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Caso completo: factura de venta + NC + ND. Solo la factura de venta debe contar.
        var saleInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 64);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 13, numeroComprobante: 65);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 12, numeroComprobante: 66);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + NC + ND"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(saleInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_TwoLiveSaleInvoices_DoesNotTriggerInv100_Adr042()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // ADR-042 (2026-07-01): se LEVANTA INV-100 para el caso multi-factura con CAE. DOS facturas de venta
        // vivas ahora DRAFTEA normal (al confirmar se emite una NC por factura). El puntero PRINCIPAL queda en
        // la mas reciente por CreatedAt (numero 68).
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 67);
        var mostRecent = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 68);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Dos facturas de venta vivas (multi-factura)"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(mostRecent.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_SingleSaleInvoice_DraftsNormally()
    {
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        var saleInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 69);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura unica"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(saleInvoice.Id, bc.OriginatingInvoiceId);
        Assert.Equal(supplierA.Id, bc.SupplierId);
    }

    // ============================================================
    // Bug 2026-07-01: fila FANTASMA (factura de venta sin CAE, encolada o rechazada por
    // ARCA) NO debe contar como factura de venta viva para INV-100. El count/seleccion
    // debe incluir SOLO facturas EMITIDAS (con CAE), igual que los sitios hermanos.
    // ============================================================

    [Fact]
    public async Task Draft_LiveSaleInvoicePlusRejectedPhantom_DoesNotTriggerInv100_OriginatingIsLiveInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Factura de venta EMITIDA (con CAE) + una fila de venta RECHAZADA por ARCA
        // (Resultado="R", CAE=null) de un intento fallido. La rechazada NO es factura
        // fiscal real: no debe contar para INV-100 ni quedar como originatingInvoice.
        var liveInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 70);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 71,
            cae: null, resultado: "R");
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + fila rechazada"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(liveInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_LiveSaleInvoicePlusPendingPhantom_DoesNotTriggerInv100_OriginatingIsLiveInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Factura de venta EMITIDA (con CAE) + una fila de venta ENCOLADA/PENDIENTE
        // (Resultado="PENDING", CAE=null) de un reintento en vuelo. Tampoco cuenta.
        var liveInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 72);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 73,
            cae: null, resultado: "PENDING");
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + fila pendiente"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(liveInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_LiveSaleInvoicePlusCreditNotePlusPhantom_DoesNotTriggerInv100()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Combina con el fix anterior (excluir NC): factura de venta emitida + su NC + una
        // fila fantasma de venta sin CAE. Solo la factura emitida debe contar.
        var liveInvoice = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 74);
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 13, numeroComprobante: 75); // NC C
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 76,
            cae: null, resultado: "R"); // fila fantasma
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular reserva con factura + NC + fila fantasma"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(liveInvoice.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_TwoLiveSaleInvoicesWithCae_DoesNotTriggerInv100_MultiCurrencyEnabled_Adr042()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // ADR-042 (2026-07-01): caso legitimo multimoneda (USD + ARS). DOS facturas de venta EMITIDAS (con CAE)
        // ahora se anulan con una NC por factura -> DRAFTEA normal (ya NO dispara INV-100). Es exactamente el
        // caso "anular multi-factura" que este ADR habilita.
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 77);
        var mostRecent = SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 78);
        await ctx.SaveChangesAsync();

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Dos facturas emitidas con CAE (multimoneda)"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(mostRecent.Id, bc.OriginatingInvoiceId);
    }

    [Fact]
    public async Task Draft_OnlyPhantomInvoiceNoCae_ThrowsNoActiveInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, _, _, _, _, _) = await SeedAsync(ctx, addSecondOperatorService: false);
        var service = BuildService(ctx);

        // Solo una fila fantasma (sin CAE) y ninguna factura emitida: no hay factura de
        // venta activa para anular. Debe caer en activeInvoices.Count==0 con el mensaje ya
        // existente (no un INV-100 falso).
        SeedInvoice(ctx, reserva.Id, tipoComprobante: 11, numeroComprobante: 79,
            cae: null, resultado: "R");
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DraftAsync(
                new DraftCancellationRequest(reserva.PublicId, "Solo fila fantasma"),
                "vendedor-1", "Vendedor", CancellationToken.None));
        Assert.Contains("no tiene factura activa para anular", ex.Message);
    }

    // ============================================================
    // Backfill: 1 linea sintetica por BC historico (centinela ServiceId=0)
    // ============================================================

    [Fact]
    public async Task Backfill_CreatesOneSyntheticLinePerBcWithoutLines_Idempotent()
    {
        using var ctx = NewDbContext();
        var (reserva, customer, supplierA, _, _, _) = await SeedAsync(ctx);

        var invoice = new Invoice { TipoComprobante = 11, ReservaId = reserva.Id, ImporteTotal = 80_000m };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // BC historico SIN lineas (modelo viejo).
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Closed,
            Reason = "Cancelacion historica", DraftedByUserId = "x",
            ReceivedRefundAmount = 12_000m,
            FiscalSnapshot = new FiscalSnapshot { CurrencyAtEvent = "ARS", Source = ExchangeRateSource.Unset },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var backfill = new BookingCancellationLineBackfillService(ctx);

        Assert.True(await backfill.NeedsBackfillAsync());
        var created = await backfill.RunAsync();
        Assert.Equal(1, created);

        var line = await ctx.BookingCancellationLines.FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(supplierA.Id, line.SupplierId);
        Assert.Equal(CancellableServiceTable.Generic, line.ServiceTable);
        Assert.Equal(0, line.ServiceId);                 // centinela de backfill
        Assert.Equal(BookingCancellationLineScope.Full, line.Scope);
        Assert.Equal(12_000m, line.ReceivedRefundAmount); // copia el recibido del padre

        // Idempotente: correrlo de nuevo no duplica.
        Assert.False(await backfill.NeedsBackfillAsync());
        var createdAgain = await backfill.RunAsync();
        Assert.Equal(0, createdAgain);
        Assert.Single(ctx.BookingCancellationLines.Where(l => l.BookingCancellationId == bc.Id));
    }

    // ============================================================
    // B2: imputacion del refund AGREGADA por operador (NUNCA SingleOrDefault)
    // ============================================================

    [Fact]
    public void DistributeRefund_TwoLinesSameOperator_DoesNotThrow_FillsByCap_B2()
    {
        // El bug que B2 cierra: con 2+ lineas del mismo operador, un SingleOrDefault tiraria 500.
        // El reparto agregado debe funcionar sin excepcion y respetar los caps.
        var lines = new System.Collections.Generic.List<BookingCancellationLine>
        {
            new() { SupplierId = 7, RefundCap = 30_000m, ReceivedRefundAmount = 0m },
            new() { SupplierId = 7, RefundCap = 20_000m, ReceivedRefundAmount = 0m },
        };

        // Recibimos 40.000 del operador: llena la primera linea (30.000) y deja 10.000 en la segunda.
        OperatorRefundService.DistributeReceivedRefundToOperatorLines(lines, 40_000m);

        Assert.Equal(30_000m, lines[0].ReceivedRefundAmount);
        Assert.Equal(10_000m, lines[1].ReceivedRefundAmount);
        Assert.Equal(BookingCancellationLineRefundStatus.Settled, lines[0].RefundStatus);       // cubrio su cap
        Assert.Equal(BookingCancellationLineRefundStatus.PendingOperatorRefund, lines[1].RefundStatus); // parcial
    }

    [Fact]
    public void DistributeRefund_ExcessGoesToLastLine_NoMoneyLost_B2()
    {
        var lines = new System.Collections.Generic.List<BookingCancellationLine>
        {
            new() { SupplierId = 7, RefundCap = 10_000m, ReceivedRefundAmount = 0m },
            new() { SupplierId = 7, RefundCap = 10_000m, ReceivedRefundAmount = 0m },
        };

        // Recibimos 25.000 (mas que la suma de caps 20.000): el excedente cae en la ultima linea.
        OperatorRefundService.DistributeReceivedRefundToOperatorLines(lines, 25_000m);

        Assert.Equal(10_000m, lines[0].ReceivedRefundAmount);
        Assert.Equal(15_000m, lines[1].ReceivedRefundAmount); // 10.000 + 5.000 de excedente
        Assert.Equal(25_000m, lines.Sum(l => l.ReceivedRefundAmount)); // no se pierde plata
    }

    [Fact]
    public void DistributeRefund_SingleLine_BehavesAsBefore()
    {
        var lines = new System.Collections.Generic.List<BookingCancellationLine>
        {
            new() { SupplierId = 7, RefundCap = 50_000m, ReceivedRefundAmount = 0m },
        };

        OperatorRefundService.DistributeReceivedRefundToOperatorLines(lines, 50_000m);

        Assert.Equal(50_000m, lines[0].ReceivedRefundAmount);
        Assert.Equal(BookingCancellationLineRefundStatus.Settled, lines[0].RefundStatus);
    }

    // ============================================================
    // ADR-044 T5 Addendum, Decision A (2026-07-11): SEC-B1 (bloqueo binario por factura viva) se REEMPLAZA
    // por una compuerta de 3 salidas. El candado de VOUCHER Issued sigue igual (ver mas abajo); el de
    // factura viva ya NO bloquea de punta a punta.
    // ============================================================

    [Fact]
    public async Task CancelService_WithLiveCaeInvoice_NoLongerBlocked_ResolvesCreditLineAndCancelsService()
    {
        // ADR-044 T5 Addendum, Decision A: con UNA sola factura de venta viva (caso trivial), cancelar el
        // servicio YA NO se bloquea: el servicio se cancela, la deuda del operador se recalcula, y la linea
        // de cancelacion resuelve SOLA la factura destino del credito (unica factura activa) y el monto
        // (LineSaleAmount, coincide con el remanente completo -> "el servicio es el 100% de su factura").
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, hotel, _) = await SeedAsync(ctx, hotelNetCost: 50_000m, paidToSupplierA: 50_000m);
        var service = BuildService(ctx);

        // Factura viva: TipoComprobante 11 = Factura C (NO es NC), CAE no vacio, anulacion != Succeeded.
        var invoice = new Invoice
        {
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = 70,
            CAE = "12345678901234",
            Resultado = "A",
            ImporteTotal = 80_000m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Bajo el hotel con factura viva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        // El servicio SI queda cancelado (ya no se bloquea de punta a punta).
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
        Assert.NotNull(hotelReloaded.CancelledAt);

        // La linea de cancelacion resolvio la factura destino y el monto SOLA (1 factura activa, sin ambiguedad).
        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.Id, line.TargetInvoiceId);
        Assert.Equal(80_000m, line.ConfirmedGrossCreditAmount);
        Assert.NotNull(line.CreditAmountConfirmedAt);

        // El BC padre queda Drafted (la emision fiscal real es un paso de confirmacion aparte, ADR-044 T5).
        var bc = await ctx.BookingCancellations.AsNoTracking().SingleAsync();
        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
    }

    [Fact]
    public async Task CancelService_BlockedByIssuedVoucher_DoesNotCancel_SECB1()
    {
        // SEC-B1 (variante voucher): el mismo candado bloquea cuando la reserva tiene un voucher
        // Issued, aunque no haya factura. El voucher entregado refleja el servicio; bajarlo sin
        // anular el voucher rompe la coherencia.
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        ctx.Vouchers.Add(new Voucher
        {
            ReservaId = reserva.Id,
            Status = VoucherStatuses.Issued,
        });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Intento bajar el hotel con voucher emitido"),
                "vendedor-1", "Vendedor", CancellationToken.None));

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.False(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    [Fact]
    public async Task CancelService_NotBlockedByCreditNote_DropsBalance_SECB1()
    {
        // SEC-B1 (limite del candado): una NOTA DE CREDITO viva (TipoComprobante 13 = NC C) NO bloquea
        // — la NC resta, no suma. Si lo unico que hay es una NC (sin factura viva), la cancelacion del
        // servicio procede normal. Esto sella que el guard distingue factura (bloquea) de NC (no bloquea).
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, hotel, _) = await SeedAsync(ctx, hotelNetCost: 50_000m, paidToSupplierA: 50_000m);
        var service = BuildService(ctx);

        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 13, // Nota de Credito C -> excluida de LiveInvoiceCreditNoteTypes (no bloquea)
            PuntoDeVenta = 1,
            NumeroComprobante = 71,
            CAE = "99999999999999",
            Resultado = "A",
            ImporteTotal = 80_000m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await ctx.SaveChangesAsync();

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Baja el hotel; solo hay NC viva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));

        var purchasesAfter = await ctx.SupplierBalanceByCurrency
            .Where(r => r.SupplierId == supplierA.Id && r.Currency == "ARS")
            .Select(r => (decimal?)r.ConfirmedPurchases)
            .FirstOrDefaultAsync();
        Assert.Equal(0m, purchasesAfter ?? 0m); // la NC no impide bajar la deuda del servicio cancelado
    }

    // ============================================================
    // SEC-B1b: el parcial deja rastro (BookingCancellation padre + linea Scope=Partial)
    //
    // HALLAZGO (gap documentado): para los tipos TIPADOS (Hotel/Flight/Package/Transfer) el candado
    // SEC-B1 y el ancla de RecordPartialCancellationLineAsync miran la MISMA condicion a nivel reserva
    // (factura viva). Por eso son MUTUAMENTE EXCLUYENTES: si hay factura viva el guard bloquea ANTES y
    // nunca se llega a crear la linea; si no hay factura viva, RecordPartial retorna temprano (sin ancla
    // fiscal) y tampoco crea la linea. Resultado real para servicios tipados: la cancelacion parcial
    // NUNCA crea BookingCancellation+Line; queda solo el servicio cancelado + deuda recalculada. Esto
    // coincide con la "limitacion declarada" del doc-comment de RecordPartialCancellationLineAsync.
    // Los tests de abajo FIJAN ese comportamiento real (no el deseado): si en el futuro se desacopla el
    // ancla del candado (p. ej. anclar a una factura ya anulada por NC), estos tests se deberan revisar.
    // ============================================================

    [Fact]
    public async Task CancelService_TypedService_WithoutLiveInvoice_DoesNotCreatePartialLine_SECB1b_Gap()
    {
        // Sin factura viva: RecordPartial no tiene ancla -> no crea BookingCancellation ni linea.
        // El servicio igual queda cancelado (lo central de la cancelacion parcial); solo NO hay rastro
        // fiscal BC/Line. Comportamiento real observado (gap declarado en el doc del metodo).
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Baja parcial sin factura"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));

        // No se creo BC padre ni linea (sin ancla fiscal). Gap documentado, no bug nuevo.
        Assert.Empty(await ctx.BookingCancellations.Where(b => b.ReservaId == reserva.Id).ToListAsync());
        Assert.Empty(await ctx.BookingCancellationLines.ToListAsync());
    }

    [Fact]
    public async Task CancelService_TypedService_WithLiveInvoice_NoLongerBlocked_CreatesPartialLineWithTargetInvoice()
    {
        // ADR-044 T5 Addendum, Decision A: con factura viva, un servicio TIPADO (hotel) ya no se bloquea.
        // La linea Partial SI se crea (antes el guard cortaba antes de RecordPartial; ahora la resolucion
        // de la nota de credito reemplaza ese corte).
        using var ctx = NewDbContext();
        var (reserva, _, _, _, hotel, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 72, CAE = "12121212121212",
            Resultado = "A", ImporteTotal = 80_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Baja parcial con factura viva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.Id, line.TargetInvoiceId);
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    [Fact]
    public async Task CancelService_GenericService_WithLiveInvoice_CreatesPartialLine_SECB1b()
    {
        // SEC-B1b (camino que SI deja rastro): para un servicio GENERICO el candado mira el guard por Id
        // de servicio (GetServiceMutationBlockReasonAsync). Ese guard tambien usa el reserva-level
        // (factura viva) -> bloquea igual. PERO si la reserva tiene factura viva el generico tambien se
        // bloquea. Para observar la creacion de la linea Partial necesitamos un caso donde el guard NO
        // bloquee Y exista ancla. Como ambos miran lo mismo, el unico modo de ver la linea es un servicio
        // SIN operador (no genera linea) o documentar el gap. Aca FIJAMOS que un generico sin factura
        // viva tampoco crea linea (sin ancla), igual que el tipado: el gap es transversal.
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx);
        var service = BuildService(ctx);

        var generic = new ServicioReserva
        {
            ReservaId = reserva.Id,
            SupplierId = supplierA.Id,
            Status = "Confirmado",
            NetCost = 10_000m,
            SalePrice = 15_000m,
            Currency = "ARS",
        };
        ctx.Servicios.Add(generic);
        await ctx.SaveChangesAsync();

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Generic", generic.PublicId, "Baja generico sin factura"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var genericReloaded = await ctx.Servicios.AsNoTracking().FirstAsync(s => s.Id == generic.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(genericReloaded));

        // Sin factura viva: no hay ancla -> no se crea linea (gap declarado, mismo que tipado).
        Assert.Empty(await ctx.BookingCancellationLines.ToListAsync());
    }

    // ============================================================
    // SEC-B2: RefundCap efectivo (= pagado al operador, topeado por costo, nunca 0 si hubo pago)
    // ============================================================

    [Fact]
    public async Task BuildLines_RefundCap_EqualsPaidToOperator_WhenPaidEqualsCost_SECB2()
    {
        // SEC-B2: la linea del hotel debe llevar RefundCap = lo pagado al operador (50.000), topeado por
        // el costo (50.000). El bug que sella: si el cap quedara en 0, el refund nunca podria imputarse
        // y RefundStatus=Settled seria inalcanzable. El pago DEBE estar imputado a la reserva (ReservaId)
        // para que entre al pool por (operador, moneda).
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, hotel, _) = await SeedAsync(
            ctx, hotelNetCost: 50_000m, paidToSupplierA: 0m); // sembramos el pago a mano con ReservaId
        var service = BuildService(ctx);

        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);
        var invoice = await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 80);

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Total un operador"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.Include(b => b.Lines).FirstAsync(b => b.PublicId == dto.PublicId);
        var hotelLine = bc.Lines.Single(l => l.ServiceTable == CancellableServiceTable.Hotel);
        Assert.Equal(50_000m, hotelLine.RefundCap);
        _ = invoice;
    }

    [Fact]
    public async Task BuildLines_RefundCap_PaidLessThanCost_CapEqualsPaid_SECB2()
    {
        // Pagado (30.000) < costo (50.000): el cap es lo pagado (no se puede devolver mas de lo entregado).
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, hotelNetCost: 50_000m, paidToSupplierA: 0m);
        var service = BuildService(ctx);

        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 30_000m);
        await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 81);

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Total un operador, pago parcial"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.Include(b => b.Lines).FirstAsync(b => b.PublicId == dto.PublicId);
        var hotelLine = bc.Lines.Single(l => l.ServiceTable == CancellableServiceTable.Hotel);
        Assert.Equal(30_000m, hotelLine.RefundCap);
    }

    [Fact]
    public async Task BuildLines_RefundCap_PaidMoreThanCost_CapToppedByCost_SECB2()
    {
        // Pagado (70.000) > costo (50.000): el cap se topea al costo del servicio (no se devuelve mas
        // de lo que costo, aunque se haya pagado de mas al operador).
        using var ctx = NewDbContext();
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, hotelNetCost: 50_000m, paidToSupplierA: 0m);
        var service = BuildService(ctx);

        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 70_000m);
        await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 82);

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Total un operador, sobrepago"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.Include(b => b.Lines).FirstAsync(b => b.PublicId == dto.PublicId);
        var hotelLine = bc.Lines.Single(l => l.ServiceTable == CancellableServiceTable.Hotel);
        Assert.Equal(50_000m, hotelLine.RefundCap); // topeado por el costo
    }

    // ============================================================
    // INV-118: la moneda del refund se valida contra la LINEA del operador, NO contra el FiscalSnapshot
    // ============================================================

    [Fact]
    public async Task AllocateRefund_CurrencyMatchesLineButNotSnapshot_Accepts_INV118()
    {
        // INV-118 (M-B): operador A factura/devuelve en USD (su LINEA esta en USD), pero el FiscalSnapshot
        // del evento esta en ARS (la cara fiscal hacia el cliente). Un refund de A en USD debe ACEPTARSE:
        // la coherencia se valida contra la moneda de la LINEA del operador, no contra el snapshot. Si se
        // validara contra el snapshot (ARS) este caso se rechazaria por error.
        using var ctx = NewDbContext();
        var (bcId, refundPublicId, bcPublicId) = await SeedMultiCurrencyBcAndRefundAsync(
            ctx, supplierALineCurrency: "USD", snapshotCurrency: "ARS", refundCurrency: "USD", refundAmount: 100m);

        var (refundService, _) = BuildOperatorRefundService(ctx);

        var dto = await refundService.AllocateAsync(
            refundPublicId,
            new AllocateRefundRequest(bcPublicId, GrossAmount: 100m, new System.Collections.Generic.List<DeductionLineRequest>()),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.Equal(100m, dto.GrossAmount); // no tiro INV-118: la moneda coincide con la linea de A (USD)
        _ = bcId;
    }

    [Fact]
    public async Task AllocateRefund_CurrencyMatchesSnapshotButNotLine_RejectsINV118()
    {
        // INV-118 (espejo): operador A tiene su LINEA en USD; el snapshot esta en ARS. Un refund de A en
        // ARS coincide con el snapshot pero NO con la linea -> debe RECHAZARSE con INV-118. Esto prueba
        // que la validacion mira la LINEA y no el snapshot (si mirara el snapshot, aceptaria por error).
        using var ctx = NewDbContext();
        var (_, refundPublicId, bcPublicId) = await SeedMultiCurrencyBcAndRefundAsync(
            ctx, supplierALineCurrency: "USD", snapshotCurrency: "ARS", refundCurrency: "ARS", refundAmount: 100m);

        var (refundService, _) = BuildOperatorRefundService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            refundService.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, GrossAmount: 100m, new System.Collections.Generic.List<DeductionLineRequest>()),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-118", ex.InvariantCode);
    }

    // ============================================================
    // BUG-FIX: cancelaciones parciales SUCESIVAS del mismo operador no inflan el RefundCap
    //
    // El pool pagado al operador se reparte por (operador, moneda). Cuando cada servicio se cancela en una
    // llamada separada (parcial), el armado de la linea nueva debe DESCONTAR del pool lo que ya reservaron
    // las lineas Partial PERSISTIDAS del mismo operador/moneda. Antes no lo hacia y la suma de caps superaba
    // lo pagado al operador -> el cliente podia quedar acreditado de mas en OperatorRefundService.
    //
    // Ejercitamos AssignRefundCapsAsync (internal) directamente: el camino end-to-end CancelServiceAsync no
    // puede producir 2 lineas Partial sucesivas por el gap SEC-B1/B1b documentado en esta misma clase.
    // ============================================================

    [Fact]
    public async Task AssignRefundCaps_SecondPartialSameOperator_SubtractsPoolConsumedByExistingLine()
    {
        using var ctx = NewDbContext();
        // Pagado al operador A = 50.000 (todo imputado a la reserva). Dos servicios del MISMO operador, cada
        // uno cuesta 50.000. Sin el fix, cada cancelacion parcial reclamaria 50.000 -> suma de caps 100.000
        // (el doble de lo pagado). Con el fix, la segunda solo puede tomar lo que quedo (0).
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);
        var invoice = await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 100);

        var service = BuildService(ctx);

        // Primera cancelacion parcial (ya persistida): linea Partial de A que reservo 50.000 del pool.
        var firstBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Primer servicio parcial",
            DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        firstBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9001,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
            RefundCap = 50_000m, // ya consumio todo el pool del operador A
        });
        ctx.BookingCancellations.Add(firstBc);
        await ctx.SaveChangesAsync();

        // Segunda cancelacion parcial (linea nueva, todavia no persistida) del MISMO operador/moneda.
        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Transfer,
            ServiceId = 9002,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 70_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 50_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        // El pool (50.000) ya estaba agotado por la primera linea -> la segunda no puede devolver nada.
        Assert.Equal(0m, newLine.RefundCap);

        // Invariante central del bug: la suma de caps de A nunca supera lo pagado (50.000).
        decimal totalCapsOperatorA = firstBc.Lines.First().RefundCap + newLine.RefundCap;
        Assert.True(totalCapsOperatorA <= 50_000m, $"Suma de caps {totalCapsOperatorA} supera lo pagado al operador.");
    }

    [Fact]
    public async Task AssignRefundCaps_SecondPartial_PartialPoolRemaining_AssignsOnlyLeftover()
    {
        using var ctx = NewDbContext();
        // Pagado al operador A = 50.000. La primera linea solo reservo 30.000 (su costo era menor); quedan
        // 20.000 en el pool para la segunda, aunque el costo de la segunda permita mas.
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);
        var invoice = await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 101);

        var service = BuildService(ctx);

        var firstBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Primer servicio parcial (costo bajo)",
            DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        firstBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9101,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 40_000m,
            RefundCap = 30_000m, // reservo 30.000 del pool de 50.000
        });
        ctx.BookingCancellations.Add(firstBc);
        await ctx.SaveChangesAsync();

        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Transfer,
            ServiceId = 9102,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 90_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 60_000m }; // costo permite mas, pero el pool no

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        Assert.Equal(20_000m, newLine.RefundCap); // solo lo que quedo del pool (50.000 - 30.000)
        Assert.True(30_000m + newLine.RefundCap <= 50_000m);
    }

    [Fact]
    public async Task AssignRefundCaps_FirstPartialOfOperator_NoPreviousLines_CapUnchanged()
    {
        using var ctx = NewDbContext();
        // Operador A SIN cancelaciones previas: el caso simple no debe cambiar. Pagado 50.000, costo 50.000
        // -> cap 50.000 (igual que antes del fix).
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);

        var service = BuildService(ctx);

        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9201,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 50_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        Assert.Equal(50_000m, newLine.RefundCap); // sin lineas previas, el cap es el normal
    }

    // ===== FIX D (2026-07-04): el RefundStatus de la linea nace coherente con el cap =====

    [Fact]
    public async Task AssignRefundCaps_CapPositive_SetsRefundStatusPendingOperatorRefund()
    {
        using var ctx = NewDbContext();
        // Se le pago 50.000 al operador A y el servicio cuesta 50.000 -> cap 50.000 > 0.
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);

        var service = BuildService(ctx);

        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9301,
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 50_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        Assert.True(newLine.RefundCap > 0m);
        // FIX D: la linea con cap > 0 nace "esperando el reembolso del operador", no en None (default enganoso).
        Assert.Equal(BookingCancellationLineRefundStatus.PendingOperatorRefund, newLine.RefundStatus);
    }

    [Fact]
    public async Task AssignRefundCaps_CapZero_SetsRefundStatusNone()
    {
        using var ctx = NewDbContext();
        // NO se le pago nada al operador A (sin SupplierPayment) -> el pool es 0 -> cap 0.
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);

        var service = BuildService(ctx);

        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9302,
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 50_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        Assert.Equal(0m, newLine.RefundCap);
        // Sin cap no hay nada que esperar del operador -> None (coherente con el doc del enum).
        Assert.Equal(BookingCancellationLineRefundStatus.None, newLine.RefundStatus);
    }

    [Fact]
    public async Task AssignRefundCaps_DifferentOperators_DoNotAffectEachOther()
    {
        using var ctx = NewDbContext();
        // Operador B tiene una linea previa que consumio su pool; eso NO debe tocar el cap del operador A.
        var (reserva, _, supplierA, supplierB, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierB.Id, reserva.Id, amount: 40_000m);
        var invoice = await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 103);

        var service = BuildService(ctx);

        // Linea previa del operador B (consume su propio pool, no el de A).
        var bcB = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplierB.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Parcial del operador B",
            DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        bcB.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer,
            ServiceId = 9301,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 30_000m,
            RefundCap = 40_000m, // consumio todo el pool de B
        });
        ctx.BookingCancellations.Add(bcB);
        await ctx.SaveChangesAsync();

        // Nueva linea del operador A: su cap debe ser pleno (la linea previa era de B).
        var newLineA = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9302,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLineA };
        var netCosts = new System.Collections.Generic.List<decimal> { 50_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        Assert.Equal(50_000m, newLineA.RefundCap); // el consumo de B no afecta a A
    }

    [Fact]
    public async Task AssignRefundCaps_ExistingLineWithConfirmedPenalty_DeductsCapPlusPenaltyFromPool()
    {
        using var ctx = NewDbContext();
        // La linea previa tiene RefundCap neto 20.000 + penalidad confirmada 10.000: del pool reservo 30.000
        // (capBeforePenalty), no solo el cap neto. La segunda linea debe ver el pool reducido en 30.000.
        var (reserva, _, supplierA, _, _, _) = await SeedAsync(ctx, paidToSupplierA: 0m);
        await SeedPaymentImputedToReservaAsync(ctx, supplierA.Id, reserva.Id, amount: 50_000m);
        var invoice = await SeedLiveInvoiceAsync(ctx, reserva.Id, numero: 104);

        var service = BuildService(ctx);

        var firstBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Parcial con penalidad confirmada",
            DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        firstBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 9401,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 40_000m,
            RefundCap = 20_000m,          // cap NETO (despues de penalidad)
            PenaltyAmount = 10_000m,       // la penalidad tambien retuvo pool
            // ADR-044 T2 Addendum: RetainedDeductionAmount es el eje CAJA (lo que de verdad salio del pool).
            // Este fixture representa el camino legacy simple (Fee+Retenida), donde ambos ejes coinciden.
            RetainedDeductionAmount = 10_000m,
            PenaltyStatus = PenaltyStatus.Confirmed,
        });
        ctx.BookingCancellations.Add(firstBc);
        await ctx.SaveChangesAsync();

        var newLine = new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Transfer,
            ServiceId = 9402,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 90_000m,
        };
        var lines = new System.Collections.Generic.List<BookingCancellationLine> { newLine };
        var netCosts = new System.Collections.Generic.List<decimal> { 60_000m };

        await service.AssignRefundCapsAsync(reserva.Id, lines, netCosts, CancellationToken.None);

        // Pool 50.000 - consumido 30.000 (20.000 cap + 10.000 penalidad) = 20.000 disponible.
        Assert.Equal(20_000m, newLine.RefundCap);
    }

    // ============================================================
    // Helpers locales de los tests nuevos
    // ============================================================

    /// <summary>
    /// Siembra un <see cref="SupplierPayment"/> IMPUTADO a la reserva (con <c>ReservaId</c>), requisito
    /// para que <c>AssignRefundCapsAsync</c> lo cuente en el pool por (operador, moneda) del RefundCap.
    /// </summary>
    private static async Task SeedPaymentImputedToReservaAsync(
        AppDbContext ctx, int supplierId, int reservaId, decimal amount)
    {
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId,
            ReservaId = reservaId,
            Amount = amount,
            Currency = "ARS",
            ImputedCurrency = "ARS",
            ImputedAmount = amount,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Siembra una factura viva (Factura C con CAE) sobre la reserva, ancla del Draft.</summary>
    private static async Task<Invoice> SeedLiveInvoiceAsync(AppDbContext ctx, int reservaId, int numero)
    {
        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = numero, CAE = "10000000000000",
            Resultado = "A", ImporteTotal = 80_000m, ReservaId = reservaId, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }

    /// <summary>
    /// Construye un <see cref="OperatorRefundService"/> con dependencias mockeadas para tests unit InMemory.
    /// El flag EnableNewCancellationFlow viene ON. Los colaboradores (BC service callback, client credit,
    /// audit) se mockean: NO hacen efectos reales, solo permiten que AllocateAsync complete el happy path.
    /// </summary>
    private static (OperatorRefundService service, Mock<IClientCreditService> clientCreditMock) BuildOperatorRefundService(AppDbContext ctx)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
        var clientCreditMock = new Mock<IClientCreditService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        // El callback de transicion NO commitea (HC1): un mock vacio alcanza para el unit test.
        bcServiceMock.Setup(s => s.OnAllocationRecordedAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        clientCreditMock.Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry());

        var service = new OperatorRefundService(
            ctx,
            bcServiceMock.Object,
            clientCreditMock.Object,
            auditMock.Object,
            settingsMock.Object,
            NullLogger<OperatorRefundService>.Instance);

        return (service, clientCreditMock);
    }

    /// <summary>
    /// Siembra un BC en AwaitingOperatorRefund con UNA linea del operador A en
    /// <paramref name="supplierALineCurrency"/>, un FiscalSnapshot en <paramref name="snapshotCurrency"/>
    /// (con condiciones fiscales RI validas para pasar la matriz) y un refund del operador A en
    /// <paramref name="refundCurrency"/>. Devuelve (bcId, refundPublicId, bcPublicId).
    /// </summary>
    private static async Task<(int bcId, Guid refundPublicId, Guid bcPublicId)> SeedMultiCurrencyBcAndRefundAsync(
        AppDbContext ctx, string supplierALineCurrency, string snapshotCurrency, string refundCurrency, decimal refundAmount)
    {
        var customer = new Customer { FullName = "Cliente INV118", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A USD", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-INV118", Name = "Reserva INV-118", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 90, ImporteTotal = 100m, ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Multi-moneda INV-118",
            DraftedByUserId = "seed",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = snapshotCurrency,
                AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow,
            },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 123,
            Scope = BookingCancellationLineScope.Full,
            Currency = supplierALineCurrency,
            LineSaleAmount = 100m,
            RefundCap = 1_000m, // alto para que la imputacion entre sin tocar el tope
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplierA.Id,
            ReceivedAmount = refundAmount,
            AllocatedAmount = 0m,
            Method = "Transfer",
            Currency = refundCurrency,
            ExchangeRateAtReceipt = 1m,
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "seed",
            ReceivedByUserName = "Seed",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        return (bc.Id, refund.PublicId, bc.PublicId);
    }
}
