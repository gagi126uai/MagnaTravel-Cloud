using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Pasos B/C — FASE C1 (2026-06-29), CODIGO DE PLATA: el reconciler del pool de saldo a favor ya NO mintea como
/// saldo a favor consumible un negativo de caja que en realidad es un REEMBOLSO POR COBRAR. Usa la formula
/// economica <c>max(0, -(Balance + MultaRetenida + ReembolsoRecibido + Y))</c>.
///
/// <para>Regresiones obligatorias: B3 (anular -> confirmar multa -> reembolso total -> cerrar BC -> pool 0) y C5
/// (cierre sin multa -> BC AbandonedByOperator con Y vivo -> pool 0, no se mintea). Mas el caso de reembolso PARCIAL
/// (BC ClientCreditApplied) que con el scope literal "open-only" del brief habria minteado el residuo: aca Y sigue
/// vivo y el pool queda en 0.</para>
/// </summary>
public class Adr041SupplierCreditReconcilerCircuitTests
{
    private const string UserId = "tester";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"recon-circuit-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static HttpContextAccessor SeeCostAccessor(out Mock<IUserPermissionResolver> resolver)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, UserId) }, "Test"))
            }
        };
        resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return accessor;
    }

    private static SupplierService SupplierSvc(AppDbContext ctx)
    {
        var accessor = SeeCostAccessor(out var resolver);
        return new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    private static SupplierCreditService CreditSvc(AppDbContext ctx)
    {
        var accessor = SeeCostAccessor(out var resolver);
        return new SupplierCreditService(ctx, new Mock<IAuditService>().Object,
            NullLogger<SupplierCreditService>.Instance, accessor, resolver.Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext ctx)
    {
        var s = new Supplier { Name = "Operador", InvoicingMode = SupplierInvoicingMode.TotalToCustomer, IsActive = true };
        ctx.Suppliers.Add(s);
        await ctx.SaveChangesAsync();
        return s;
    }

    /// <summary>Servicio CANCELADO (no cuenta como compra) + pago vivo: deja la caja en -paid tras UpdateBalance.</summary>
    private static async Task SeedAnnulledPaidAsync(AppDbContext ctx, int supplierId, int reservaId, decimal paid)
    {
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, HotelName = "H", City = "C",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Cancelado", NetCost = paid, SalePrice = paid * 1.5m, Currency = "ARS"
        });
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = reservaId, Amount = paid, Currency = "ARS", Method = "T"
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<(Reserva Reserva, Customer Customer)> AddReservaAsync(AppDbContext ctx, string numero)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, customer);
    }

    private static async Task<BookingCancellation> SeedBcAsync(
        AppDbContext ctx, int supplierId, int reservaId, int customerId,
        BookingCancellationStatus status, PenaltyStatus penaltyStatus, CancellationConceptKind conceptKind,
        decimal refundCap, decimal? penalty, decimal received)
    {
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        var bc = new BookingCancellation
        {
            ReservaId = reservaId, CustomerId = customerId, SupplierId = supplierId, OriginatingInvoiceId = invoice.Id,
            Status = status, PenaltyStatus = penaltyStatus, ConceptKind = conceptKind,
            PenaltyConfirmedAt = penaltyStatus == PenaltyStatus.Confirmed ? DateTime.UtcNow : (DateTime?)null,
            Reason = "anulacion", DraftedByUserId = "v",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        // ADR-044 T2 Addendum: RetainedDeductionAmount es el eje CAJA (lo que de verdad salio del RefundCap /
        // lo que el circuit reader pinta como "Multa retenida"). Este helper representa el camino legacy simple
        // (Fee+Retenida): coincide con PenaltyAmount SOLO cuando la penalidad es pass-through confirmada (mismo
        // gate que antes miraba bc.PenaltyStatus/bc.ConceptKind); para agency-owned o no confirmada, el operador
        // nunca retuvo nada, asi que queda en 0 (igual que antes de esta tanda, donde no se neteaba el cap).
        var retainedDeductionAmount = penaltyStatus == PenaltyStatus.Confirmed
            && conceptKind == CancellationConceptKind.OperatorPenaltyPassThrough
                ? penalty ?? 0m
                : 0m;
        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierId, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1, Scope = BookingCancellationLineScope.Full, Currency = "ARS", LineSaleAmount = refundCap,
            RefundCap = refundCap, PenaltyAmount = penalty, RetainedDeductionAmount = retainedDeductionAmount,
            ReceivedRefundAmount = received,
        });
        await ctx.SaveChangesAsync();
        return bc;
    }

    private static async Task<decimal> PoolAsync(AppDbContext ctx, int supplierId) =>
        (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync())
            .Sum(e => e.RemainingBalance);

    /// <summary>Reserva VIVA (Confirmed) con cliente, para escenarios de cancelacion parcial / draft sobre file vivo.</summary>
    private static async Task<(Reserva Reserva, Customer Customer)> AddLiveReservaAsync(AppDbContext ctx, string numero)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, customer);
    }

    /// <summary>
    /// Siembra un HOTEL con el estado dado + un BC en Drafted + su LINEA (Scope=Partial) ligada al hotel por Id real.
    /// Modela el estado post-<c>CancelServiceAsync</c> (cancelacion parcial): la BC queda en Drafted con la linea que
    /// lleva RefundCap, apuntando al servicio cuyo estado decide si Y cuenta.
    /// </summary>
    private static async Task<BookingCancellation> SeedHotelDraftBcAsync(
        AppDbContext ctx, int supplierId, int reservaId, int customerId, string hotelStatus, decimal refundCap)
    {
        var hotel = new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, HotelName = "H", City = "C",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = hotelStatus, NetCost = refundCap, SalePrice = refundCap * 1.5m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reservaId, CustomerId = customerId, SupplierId = supplierId, OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted, Reason = "cancelacion parcial", DraftedByUserId = "v",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierId, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotel.Id, Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = refundCap, RefundCap = refundCap, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();
        return bc;
    }

    // ============================================================
    // B3: anulado pagado 1000 + multa 300 confirmada + reembolso total 700 + BC Closed -> pool 0
    // ============================================================
    [Fact]
    public async Task B3_annulled_paid_confirmedPenalty_fullRefund_closed_poolIsZero()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-B3");
        await SeedAnnulledPaidAsync(ctx, supplier.Id, reserva.Id, paid: 1000m);
        await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.Closed, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough,
            refundCap: 700m, penalty: 300m, received: 700m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(-1000m, (await ctx.SupplierBalanceByCurrency.AsNoTracking().FirstAsync(r => r.SupplierId == supplier.Id)).Balance);

        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Balance -1000, pero Multa 300 + Reembolso 700 lo explican y Y=0 (BC cerrada): pool 0, NO se mintea la fuga.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // B3 intermedio: anulado pagado 1000, multa 300, SIN reembolso, BC AwaitingOperatorRefund -> pool 0 (Y=700)
    // ============================================================
    [Fact]
    public async Task B3_annulled_paid_confirmedPenalty_noRefund_awaiting_poolIsZero_byReceivable()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-B3b");
        await SeedAnnulledPaidAsync(ctx, supplier.Id, reserva.Id, paid: 1000m);
        await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough,
            refundCap: 700m, penalty: 300m, received: 0m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Balance -1000, Multa 300, Y=700 (por cobrar): -1000+300+700 = 0 -> pool 0. El negativo es receivable, no prepago.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // C5: cierre SIN multa (Waived) + sin reembolso + BC AbandonedByOperator -> pool 0 (Y entero vivo)
    // ============================================================
    [Fact]
    public async Task C5_waivedNoPenalty_abandoned_keepsReceivable_poolNotMinted()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-C5");
        await SeedAnnulledPaidAsync(ctx, supplier.Id, reserva.Id, paid: 1000m);
        // Waive: cap COMPLETO (1000, el operador devuelve todo), sin multa, sin reembolso, BC AbandonedByOperator.
        var bc = await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AbandonedByOperator, PenaltyStatus.Waived, CancellationConceptKind.OperatorPenaltyPassThrough,
            refundCap: 1000m, penalty: null, received: 0m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Y = 1000 (AbandonedByOperator sigue contando): -1000 + 0 + 0 + 1000 = 0 -> pool 0. No se mintea.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
        // El estado de la BC NO se toca: sigue AbandonedByOperator con su receivable vivo.
        var fresh = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AbandonedByOperator, fresh.Status);
    }

    // ============================================================
    // Reembolso PARCIAL (BC ClientCreditApplied): Y SIGUE vivo -> pool 0 (no se mintea el residuo)
    // ============================================================
    [Fact]
    public async Task PartialRefund_clientCreditApplied_keepsReceivable_poolNotMinted()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-PARC");
        await SeedAnnulledPaidAsync(ctx, supplier.Id, reserva.Id, paid: 1000m);
        // Multa 300, cap 700, recibido PARCIAL 400, BC ClientCreditApplied (1ra imputacion).
        await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.ClientCreditApplied, PenaltyStatus.Confirmed, CancellationConceptKind.OperatorPenaltyPassThrough,
            refundCap: 700m, penalty: 300m, received: 400m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Balance -1000, Multa 300, Reembolso 400, Y = 700-400 = 300: -1000+300+400+300 = 0 -> pool 0.
        // Con el scope literal "open-only" (que excluye ClientCreditApplied) Y seria 0 y se mintearia 300: la fuga.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // BLOQUEANTE de review (plata viva): BC Closed SUB-reembolsada -> pool 0 (el residuo NO se mintea).
    // Escenario verificado: anulado pagado 1000, sin multa, operador reembolsa 400, cliente consume los 400, BC->Closed.
    // ============================================================
    [Fact]
    public async Task ClosedBc_underRefunded_residualStaysReceivable_poolNotMinted()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "R-CLpart");
        await SeedAnnulledPaidAsync(ctx, supplier.Id, reserva.Id, paid: 1000m);
        // Sin multa, cap = lo pagado (1000), recibido PARCIAL 400, BC YA Closed (el cliente consumio su credito).
        await SeedBcAsync(ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.Closed, PenaltyStatus.Waived, CancellationConceptKind.OperatorPenaltyPassThrough,
            refundCap: 1000m, penalty: null, received: 400m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Balance -1000, Reembolso 400, Y = 1000-400 = 600 (Closed sub-reembolsada SIGUE contando): suma 0 -> pool 0.
        // Sin el fix, Y=0 y se mintearia 600 que el operador nunca devolvio.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // Sobrepago GENUINO (sin anulacion) sigue minteando el pool (la formula colapsa a max(0,-Balance))
    // ============================================================
    [Fact]
    public async Task GenuineOverpayment_noCancellation_stillMintsPool()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, _) = await AddReservaAsync(ctx, "R-OV");
        // Reserva VIVA: la compra confirmada SI cuenta como deuda (una Cancelled no contaria).
        reserva.Status = EstadoReserva.Confirmed;
        await ctx.SaveChangesAsync();
        // Servicio VIVO (Confirmado) de 1000 + pago 1500 -> sobrepago genuino 500.
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "H", City = "C",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Confirmado", NetCost = 1000m, SalePrice = 1500m, Currency = "ARS"
        });
        ctx.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 1500m, Currency = "ARS", Method = "T" });
        await ctx.SaveChangesAsync();

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // Sin circuito (no hay anulacion): overpayment = max(0,-(-500)) = 500. Saldo a favor consumible legitimo.
        Assert.Equal(500m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // TERCER CAMINO (bloqueante de review): cancelacion PARCIAL deja la BC en Drafted con el SERVICIO ya cancelado
    // y caja negativa. Un PAGO al operador (trigger REAL del reconcile) NO debe mintear el pagado. El fix de raiz
    // (Y atado al servicio cancelado, NO a bc.Status) cuenta el receivable aunque la BC siga en Drafted.
    // ============================================================
    [Fact]
    public async Task PartialCancellation_draftedBc_cancelledService_supplierPaymentTrigger_doesNotMint()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddLiveReservaAsync(ctx, "R-PARTIAL");

        // Estado post-CancelServiceAsync: hotel CANCELADO (servicio dejo de contar como compra) + BC en Drafted
        // con su linea parcial (RefundCap = 50.000). La reserva sigue VIVA (cancelacion parcial).
        await SeedHotelDraftBcAsync(ctx, supplier.Id, reserva.Id, customer.Id, hotelStatus: "Cancelado", refundCap: 50_000m);

        // Trigger REAL: un PAGO al operador por 50.000. AddSupplierPaymentAsync materializa la caja (-50.000, el
        // hotel cancelado no cuenta) y DISPARA el reconcile en la misma operacion (SupplierService:867).
        var paymentRequest = new SupplierPaymentRequest(
            Amount: 50_000m, Method: "Transferencia", Reference: null, Notes: null,
            ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true, Currency: "ARS");
        await SupplierSvc(ctx).AddSupplierPaymentAsync(supplier.Id, paymentRequest, CancellationToken.None);

        // El servicio de la linea esta cancelado -> Y cuenta 50.000 -> Prepago 0 -> pool 0. Sin el fix (Drafted en
        // el viejo deny-set), Y=0 y el reconcile del PAGO mintearia 50.000 GASTABLES.
        Assert.Equal(-50_000m, (await ctx.SupplierBalanceByCurrency.AsNoTracking().FirstAsync(r => r.SupplierId == supplier.Id)).Balance);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));

        // El extracto muestra "me tiene que devolver", NO "saldo a favor".
        var statement = await SupplierSvc(ctx).GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var ars = statement.Currencies.Single(c => c.Currency == "ARS");
        Assert.Equal(50_000m, ars.TheyOweMe);
        Assert.Equal(0m, ars.Prepayment);
    }

    // ============================================================
    // Negativo (no sobre-declarar): un draft de cancelacion TOTAL NO confirmado tiene los servicios VIVOS
    // (la caja NO es negativa por ellos) -> Y NO debe contar (sin falso "me tiene que devolver").
    // ============================================================
    [Fact]
    public async Task DraftTotalCancellation_servicesAlive_noFalseReceivable()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddLiveReservaAsync(ctx, "R-DRAFT");

        // Draft TOTAL no confirmado: el hotel sigue CONFIRMADO (cuenta como compra, caja NO negativa). La BC esta
        // en Drafted con su linea (RefundCap), pero la cancelacion todavia no tomo efecto.
        await SeedHotelDraftBcAsync(ctx, supplier.Id, reserva.Id, customer.Id, hotelStatus: "Confirmado", refundCap: 50_000m);

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var statement = await SupplierSvc(ctx).GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var ars = statement.Currencies.Single(c => c.Currency == "ARS");
        // El servicio sigue contando como compra -> Y excluido -> NO hay falso receivable. La deuda viva (le debo)
        // es la compra confirmada (50.000), sin pagos.
        Assert.Equal(0m, ars.TheyOweMe);
        Assert.Equal(50_000m, ars.ITheyOwe);
        Assert.Equal(0m, ars.Prepayment);
    }

    // ============================================================
    // R2 (endurecimiento): corner de divergencia entre los DOS filtros del lado-caja. Un servicio que SIGUE
    // contando por su estado (Confirmado) pero cuya reserva esta en un status que la caja EXCLUYE
    // (PendingOperatorRefund) -> la compra cae de la caja igual. La guarda SOBRE-DECLARA Y (cuadra con la caja),
    // NUNCA mintea. Hoy inalcanzable; congela la direccion segura a futuro.
    // ============================================================
    [Fact]
    public async Task DebtFilterDivergence_serviceCountsButReservaExcluded_overDeclaresY_doesNotMint()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        // Reserva en un status que la caja EXCLUYE (no esta en ValidReservationStatuses), pero el servicio sigue
        // Confirmado (CountsForSupplierDebtByType == true) -> el corner artificial que hoy no ocurre.
        var reserva = new Reserva { NumeroReserva = "R-DIV", Name = "R-DIV", PayerId = customer.Id, Status = EstadoReserva.PendingOperatorRefund };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "H", City = "C",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Confirmado", NetCost = 50_000m, SalePrice = 75_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        ctx.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS", Method = "T" });
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id, OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "corner", DraftedByUserId = "v",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotel.Id, Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            LineSaleAmount = 50_000m, RefundCap = 50_000m, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        await SupplierSvc(ctx).UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        // La caja excluyo la compra (reserva PendingOperatorRefund no esta en ValidReservationStatuses): balance = -50.000.
        Assert.Equal(-50_000m, (await ctx.SupplierBalanceByCurrency.AsNoTracking().FirstAsync(r => r.SupplierId == supplier.Id)).Balance);

        await CreditSvc(ctx).ReconcileSupplierCreditAsync(supplier.Id, CancellationToken.None);

        // La guarda sobre-declara Y (50.000) aunque el servicio "cuente" por su estado, porque la reserva lo
        // saca de la caja -> Prepago 0 -> pool 0. Sin la guarda, Y=0 y se mintearia 50.000.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));

        var statement = await SupplierSvc(ctx).GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var ars = statement.Currencies.Single(c => c.Currency == "ARS");
        Assert.Equal(50_000m, ars.TheyOweMe);
        Assert.Equal(0m, ars.Prepayment);
    }
}
