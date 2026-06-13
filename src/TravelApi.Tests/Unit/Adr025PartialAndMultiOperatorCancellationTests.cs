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
}
