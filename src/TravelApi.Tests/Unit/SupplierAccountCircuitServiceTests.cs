using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Pasos B/C "cuenta del operador" (2026-06-29) + SALDO UNICO (2026-06-30) — cobertura ATRAVESANDO el servicio
/// (InMemory):
///  - el extracto del operador expone los DOS numeros ("Le debo X" / "Me tiene que devolver Y") y ahora un
///    SALDO UNICO: las lineas del "Circuito de cancelacion" (multa retenida + reembolso recibido) viven
///    INTERCALADAS en <c>Lines</c> con running balance real, y el saldo MOSTRADO (<c>ClosingBalance</c>) es el
///    ECONOMICO (caja + circuito), que reconcilia con el header. El saldo de SOLO caja se preserva en
///    <c>CashClosingBalance</c> (invariante con la proyeccion). El bloque legacy <c>CircuitLines</c> queda vacio;
///  - el fix C2 (concept-aware): una penalidad AGENCY-OWNED NO reduce el RefundCap del operador (debe reembolsar
///    integro) y su cuenta CIERRA (no se mintea saldo a favor fantasma);
///  - el scope temporal del circuito: multa/reembolso permanecen al cerrar la BC, pero Y cae a 0 (Closed); un
///    reembolso PARCIAL (BC en ClientCreditApplied) mantiene Y vivo (evita re-mintear la fuga);
///  - coexistencia X e Y en la misma moneda: el saldo unico cierra en el NETO (X - Y) y el header los separa.
/// </summary>
public class SupplierAccountCircuitServiceTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-circuit-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static SupplierService SeeCostSupplierService(AppDbContext ctx)
    {
        const string userId = "tester";
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost, Permissions.TesoreriaSupplierPayments };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    private static BookingCancellationService BuildBcService(AppDbContext ctx)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });
        return new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settings.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext ctx)
    {
        var s = new Supplier { Name = "Operador", InvoicingMode = SupplierInvoicingMode.TotalToCustomer, IsActive = true };
        ctx.Suppliers.Add(s);
        await ctx.SaveChangesAsync();
        return s;
    }

    /// <summary>Siembra un servicio CANCELADO (no cuenta como compra) + un pago vivo: deja la caja en -paid.</summary>
    private static async Task SeedAnnulledPaidServiceAsync(AppDbContext ctx, int supplierId, int reservaId, decimal paid, string currency)
    {
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, HotelName = "Hotel", City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Cancelado", NetCost = paid, SalePrice = paid * 1.5m, Currency = currency
        });
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = reservaId, Amount = paid, Currency = currency, Method = "Transferencia"
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<(BookingCancellation Bc, Reserva Reserva)> SeedBcAsync(
        AppDbContext ctx, int supplierId, int reservaId, int customerId,
        BookingCancellationStatus status,
        PenaltyStatus penaltyStatus,
        CancellationConceptKind conceptKind)
    {
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoice.Id,
            Status = status,
            PenaltyStatus = penaltyStatus,
            ConceptKind = conceptKind,
            PenaltyConfirmedAt = penaltyStatus == PenaltyStatus.Confirmed ? DateTime.UtcNow : (DateTime?)null,
            Reason = "anulacion con refund esperado",
            DraftedByUserId = "v",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        var reserva = await ctx.Reservas.FirstAsync(r => r.Id == reservaId);
        return (bc, reserva);
    }

    private static void AddLine(AppDbContext ctx, int bcId, int supplierId, decimal refundCap, decimal? penalty, decimal received, string currency)
    {
        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bcId, SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = currency, LineSaleAmount = refundCap,
            RefundCap = refundCap, PenaltyAmount = penalty, ReceivedRefundAmount = received,
        });
    }

    private static async Task<(Reserva Reserva, Customer Customer)> AddReservaAsync(AppDbContext ctx, string numero)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var reserva = new Reserva { NumeroReserva = numero, Name = "Reserva " + numero, PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, customer);
    }

    private static SupplierAccountStatementCurrencyBlockDto Block(SupplierAccountStatementDto dto, string currency)
        => dto.Currencies.Single(c => c.Currency == currency);

    // ============================================================
    // B1 end-to-end: M-A (pagado 1000, multa 300 pass-through confirmada, sin reembolso)
    // ============================================================
    [Fact]
    public async Task Statement_passThroughPenalty_showsTwoNumbers_andCircuitBlock()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-MA");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 700m, penalty: 300m, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        Assert.Equal(-1000m, ars.CashClosingBalance);       // SOLO caja: el pago (eco de la proyeccion)
        Assert.Equal(-700m, ars.ClosingBalance);            // saldo unico MOSTRADO = economico
        Assert.Equal(-700m, ars.EconomicClosingBalance);    // -1000 + 300 (multa retenida)
        Assert.Equal(700m, ars.TheyOweMe);                  // me tiene que devolver
        Assert.Equal(0m, ars.ITheyOwe);                     // no le debo
        Assert.Equal(0m, ars.Prepayment);                   // no se mintea saldo a favor

        // Saldo unico: la multa retenida ahora vive INTERCALADA en Lines (ya no en el bloque aparte).
        var penaltyLine = Assert.Single(ars.Lines.Where(l => l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained));
        Assert.Equal(300m, penaltyLine.Charge);
        Assert.Empty(ars.CircuitLines);                     // bloque legacy vaciado
        // El running balance de la ultima linea cierra en el economico y coincide con el saldo mostrado.
        Assert.Equal(-700m, ars.Lines[^1].RunningBalance);
        Assert.Equal(ars.EconomicClosingBalance, ars.ClosingBalance);
    }

    // ============================================================
    // C2 end-to-end: penalidad AGENCY-OWNED no entra al circuito del operador y la cuenta CIERRA
    // ============================================================
    [Fact]
    public async Task Statement_agencyOwnedPenalty_operatorOwesFull_noPhantomCredit_noCircuitPenalty()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-AO");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        // Agency-owned: tras el fix C2, el RefundCap queda INTEGRO (1000) y PenaltyAmount sin setear (null).
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.AgencyManagementFee);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 1000m, penalty: null, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        Assert.Equal(-1000m, ars.CashClosingBalance);       // sin circuito -> caja == economico
        Assert.Equal(-1000m, ars.ClosingBalance);           // saldo unico = economico
        Assert.Equal(-1000m, ars.EconomicClosingBalance);   // sin multa en el circuito del operador
        Assert.Equal(1000m, ars.TheyOweMe);                 // el operador debe reembolsar INTEGRO
        Assert.Equal(0m, ars.ITheyOwe);
        Assert.Equal(0m, ars.Prepayment);                   // CIERRA: nada de saldo a favor fantasma
        // Una penalidad agency-owned NO entra al circuito del operador: ni en Lines ni en el legacy CircuitLines.
        Assert.DoesNotContain(ars.Lines, l => l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained);
        Assert.Empty(ars.CircuitLines);
    }

    // ============================================================
    // C2 unit: AllocateConfirmedPenaltyToLinesAsync es concept-aware
    // ============================================================
    [Fact]
    public async Task AllocatePenalty_agencyOwned_doesNotReduceRefundCap()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-C2");
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.AgencyCancellationFee);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 1000m, penalty: null, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 300m, requestedPenaltyCurrency: "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(1000m, line.RefundCap);   // INTEGRO (no se redujo)
        Assert.Null(line.PenaltyAmount);       // sin setear
    }

    [Fact]
    public async Task AllocatePenalty_passThrough_reducesRefundCap()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-PT");
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 1000m, penalty: null, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 300m, requestedPenaltyCurrency: "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(700m, line.RefundCap);     // reducido por la multa
        Assert.Equal(300m, line.PenaltyAmount);
    }

    // ============================================================
    // Scope: BC Closed conserva multa/reembolso, pero Y cae a 0 (cuenta en cero, no se re-mintea)
    // ============================================================
    [Fact]
    public async Task Statement_closedBc_keepsCircuit_butY_isZero_andCloses()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-CL");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        // Reembolso TOTAL recibido y BC Closed: multa 300 + recibido 700 = 1000.
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.Closed, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 700m, penalty: 300m, received: 700m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        Assert.Equal(-1000m, ars.CashClosingBalance);  // solo caja: el pago
        Assert.Equal(0m, ars.ClosingBalance);          // saldo unico = economico
        Assert.Equal(0m, ars.EconomicClosingBalance);  // -1000 + 300 + 700
        Assert.Equal(0m, ars.TheyOweMe);               // totalmente reembolsada: Y se auto-anula (recibido == cap)
        Assert.Equal(0m, ars.ITheyOwe);
        Assert.Equal(0m, ars.Prepayment);              // cierra en cero, no re-mintea
        // multa + reembolso siguen visibles, ahora INTERCALADOS en Lines (no en el bloque aparte).
        int circuitInLines = ars.Lines.Count(l =>
            l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained ||
            l.Kind == SupplierAccountStatementLineKinds.RefundReceived);
        Assert.Equal(2, circuitInLines);
        Assert.Empty(ars.CircuitLines);
        Assert.Equal(0m, ars.Lines[^1].RunningBalance); // el saldo unico cierra en cero
    }

    // ============================================================
    // BLOQUEANTE de review (plata viva): BC Closed SUB-reembolsada -> el residuo se muestra como
    // "Me tiene que devolver" (Y), NO como saldo a favor (Prepayment), y el pool no se mintea.
    // Escenario: anulado pagado 1000, sin multa, operador reembolsa 400, cliente consume 400, BC->Closed.
    // ============================================================
    [Fact]
    public async Task Statement_closedBc_underRefunded_residualShownAsTheyOweMe_notPrepayment()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-CLpart");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        // Sin multa, cap = pagado (1000), recibido PARCIAL 400, BC Closed (cliente ya consumio su credito).
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.Closed, PenaltyStatus.Waived, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 1000m, penalty: null, received: 400m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        // Econ = -1000 + 400 (reembolso) = -600. Y = 1000-400 = 600 (residuo del operador, todavia vivo).
        Assert.Equal(-1000m, ars.CashClosingBalance);
        Assert.Equal(-600m, ars.ClosingBalance);          // saldo unico = economico
        Assert.Equal(-600m, ars.EconomicClosingBalance);
        Assert.Equal(600m, ars.TheyOweMe);   // el residuo se muestra como "me tiene que devolver"
        Assert.Equal(0m, ars.ITheyOwe);
        Assert.Equal(0m, ars.Prepayment);    // NO se mintea saldo a favor: el operador nunca devolvio esos 600
        Assert.Equal(-600m, ars.Lines[^1].RunningBalance);
    }

    // ============================================================
    // Scope: reembolso PARCIAL deja la BC en ClientCreditApplied y Y SIGUE vivo (no se mintea el residuo)
    // ============================================================
    [Fact]
    public async Task Statement_partialRefund_clientCreditApplied_keepsY_noPhantomCredit()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-PARC");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        // Multa 300 confirmada, cap 700, recibido PARCIAL 400. La BC ya esta en ClientCreditApplied (1ra imputacion).
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.ClientCreditApplied, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 700m, penalty: 300m, received: 400m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        // Econ = -1000 + 300 (multa) + 400 (recibido) = -300. Y = 700 - 400 = 300. X = max(0,-300+300)=0, Prepago=0.
        Assert.Equal(-1000m, ars.CashClosingBalance);
        Assert.Equal(-300m, ars.ClosingBalance);          // saldo unico = economico
        Assert.Equal(-300m, ars.EconomicClosingBalance);
        Assert.Equal(300m, ars.TheyOweMe);   // el receivable parcial SIGUE vivo (no se mintea el residuo)
        Assert.Equal(0m, ars.ITheyOwe);
        Assert.Equal(0m, ars.Prepayment);
        Assert.Equal(-300m, ars.Lines[^1].RunningBalance);
    }

    // ============================================================
    // SALDO UNICO — coexistencia X e Y en la misma moneda: una deuda de compra VIVA (payable) + un receivable de
    // anulacion (el operador debe devolver). El saldo unico cierra en el NETO (X - Y) y el header los separa.
    // Escenario: reserva viva con compra confirmada IMPAGA 500 (payable) + otra reserva anulada, pagada 1000,
    // multa 300 pass-through confirmada, sin reembolso (receivable 700).
    // ============================================================
    [Fact]
    public async Task Statement_coexistingPayableAndReceivable_singleBalanceNets_headerSeparatesXandY()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);

        // (1) Reserva VIVA (Confirmed) con una compra confirmada IMPAGA -> deuda de compra viva (payable +500).
        var liveReserva = new Reserva { NumeroReserva = "R-LIVE", Name = "Reserva viva", Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(liveReserva);
        await ctx.SaveChangesAsync();
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = liveReserva.Id, SupplierId = supplier.Id, HotelName = "Hotel vivo", City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Confirmado", NetCost = 500m, SalePrice = 750m, Currency = "ARS"
        });
        await ctx.SaveChangesAsync();

        // (2) Reserva ANULADA con servicio cancelado pagado 1000 + BC con multa 300 (receivable 700, sin reembolso).
        var (annulReserva, customer) = await AddReservaAsync(ctx, "R-ANN");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, annulReserva.Id, paid: 1000m, currency: "ARS");
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, annulReserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 700m, penalty: 300m, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        var service = SeeCostSupplierService(ctx);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        var ars = Block(statement, "ARS");
        // Caja = +500 (compra viva) - 1000 (pago anulacion) = -500. Econ = -500 + 300 (multa) = -200.
        Assert.Equal(-500m, ars.CashClosingBalance);
        Assert.Equal(-200m, ars.EconomicClosingBalance);
        Assert.Equal(-200m, ars.ClosingBalance);            // saldo unico = NETO (X - Y = 500 - 700 = -200)
        Assert.Equal(500m, ars.ITheyOwe);                   // le debo (la compra viva impaga)
        Assert.Equal(700m, ars.TheyOweMe);                  // me tiene que devolver (el receivable de la anulacion)
        Assert.Equal(0m, ars.Prepayment);                   // X e Y no se netean en el header, pero X>0 => sin prepago
        Assert.Equal(-200m, ars.Lines[^1].RunningBalance);  // el saldo unico cierra en el economico
    }

    // ============================================================
    // SALDO UNICO — masking: sin see_cost, el saldo unico y todas las columnas van a 0, pero la ESTRUCTURA
    // (los movimientos de caja + circuito intercalados) sigue visible.
    // ============================================================
    [Fact]
    public async Task Statement_withCircuit_withoutSeeCost_masksAmounts_keepsMergedStructure()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-MASK");
        await SeedAnnulledPaidServiceAsync(ctx, supplier.Id, reserva.Id, paid: 1000m, currency: "ARS");
        var (bc, _) = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough);
        AddLine(ctx, bc.Id, supplier.Id, refundCap: 700m, penalty: 300m, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        // Caller SIN see_cost (solo tesoreria no alcanza para ver costos).
        const string userId = "masked";
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string>();
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        var maskedService = new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);

        var statement = await maskedService.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        Assert.False(statement.AmountsVisible);
        var ars = Block(statement, "ARS");
        // Estructura visible: el pago (caja) + la multa (circuito) intercalados = 2 movimientos.
        Assert.Equal(2, ars.Lines.Count);
        Assert.All(ars.Lines, l =>
        {
            Assert.Equal(0m, l.Charge);
            Assert.Equal(0m, l.Credit);
            Assert.Equal(0m, l.RunningBalance);
        });
        Assert.Equal(0m, ars.ClosingBalance);
        Assert.Equal(0m, ars.CashClosingBalance);
        Assert.Equal(0m, ars.EconomicClosingBalance);
        Assert.Equal(0m, ars.TheyOweMe);
        Assert.Equal(0m, ars.ITheyOwe);
    }
}
