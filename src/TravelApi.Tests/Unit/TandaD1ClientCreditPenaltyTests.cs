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
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda D1 (2026-07-16): saldo a favor del cliente APLICADO CONTRA UNA MULTA (Nota de Debito de una reserva
/// anulada) + neteo automatico en devolucion. Cubre: gate CAE (ND aprobada con comprobante vigente), tope contra
/// el saldo pendiente REAL de la ND, cross-currency bloqueado, FIFO de bolsillos, pool insuficiente, cliente
/// equivocado, reversa (anti doble-reversa), invisibilidad del puente en la pestaña Pagos del cliente, preview
/// de neteo (credito&gt;deuda / deuda&gt;credito / neto 0) y la devolucion con neteo end-to-end.
///
/// <para>NOTA InMemory: la atomicidad real (transaccion Serializable) y el retry ante conflicto de
/// serializacion viven en integracion Postgres (no disponible en este entorno, Docker off). Aca se valida la
/// LOGICA de negocio: gates, drenaje FIFO, tope, reversa, invisibilidad y armado del neteo.</para>
/// </summary>
public class TandaD1ClientCreditPenaltyTests
{
    private const string UserId = "tester";

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ClientCreditService CreateService(
        AppDbContext context, Mock<IAuditService>? audit = null, bool enableNewCancellationFlow = true)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = enableNewCancellationFlow });

        return new ClientCreditService(
            context,
            bcService: new Mock<IBookingCancellationService>().Object,
            approvalService: new Mock<IApprovalRequestService>().Object,
            auditService: (audit ?? new Mock<IAuditService>()).Object,
            settings: settings.Object,
            logger: NullLogger<ClientCreditService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null);
    }

    /// <summary>
    /// Fix N2 (post-review): service con HttpContext + resolver mockeados para simular un vendedor LOGUEADO
    /// con scope ACOTADO (no ve todas las cobranzas). Mismo patron que
    /// <c>Fc4ClientCreditApplicationTests.CreateScopedService</c> — sirve para probar que el NETEO ignora el
    /// scope del vendedor (a diferencia del endpoint de aplicar/revertir, que si lo respeta).
    /// </summary>
    private static ClientCreditService CreateScopedService(AppDbContext context, string userId, bool seesAllCobranzas)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = seesAllCobranzas
            ? new HashSet<string> { Permissions.CobranzasViewAll }
            : new HashSet<string>();
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        return new ClientCreditService(
            context,
            bcService: new Mock<IBookingCancellationService>().Object,
            approvalService: new Mock<IApprovalRequestService>().Object,
            auditService: new Mock<IAuditService>().Object,
            settings: settings.Object,
            logger: NullLogger<ClientCreditService>.Instance,
            permissionResolver: resolver.Object,
            httpContextAccessor: accessor);
    }

    private static async Task<Customer> AddCustomerAsync(AppDbContext context, string name = "Cliente")
    {
        var customer = new Customer { FullName = name };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<ClientCreditEntry> AddCreditEntryAsync(
        AppDbContext context, int customerId, decimal amount, string? currency = null, DateTime? createdAt = null)
    {
        var entry = new ClientCreditEntry
        {
            CustomerId = customerId,
            Currency = currency ?? Monedas.ARS,
            CreditedAmount = amount,
            RemainingBalance = amount,
            IsFullyConsumed = false,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        context.ClientCreditEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    /// <summary>
    /// Reserva ANULADA con una multa CONFIRMADA cuya ND ya tiene CAE (Issued) — el caso "abierto y cobrable".
    /// <paramref name="reservaStatus"/> y <paramref name="responsibleUserId"/> son opcionales: sirven para
    /// armar los casos N2 (multa en reserva de OTRO vendedor) y N3 (multa de cancelacion PARCIAL sobre una
    /// reserva que sigue VIVA, no Cancelled).
    /// </summary>
    private static async Task<(Reserva Reserva, Invoice Nd, BookingCancellation Bc)> SeedIssuedPenaltyAsync(
        AppDbContext context, int customerId, string numeroReserva, decimal ndAmount, string monId = "PES",
        int numeroComprobante = 500, string? reservaStatus = null, string? responsibleUserId = null)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numeroReserva,
            Name = "Reserva " + numeroReserva,
            PayerId = customerId,
            Status = reservaStatus ?? EstadoReserva.Cancelled,
            ResponsibleUserId = responsibleUserId,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var nd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            Resultado = "A",
            ImporteTotal = ndAmount,
            MonId = monId,
            ReservaId = reserva.Id,
            CAE = "77777777",
            AnnulmentStatus = AnnulmentStatus.None,
        };
        context.Invoices.Add(nd);
        await context.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            Reason = "Cliente anuló el viaje",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyStatus = PenaltyStatus.Confirmed,
            DebitNoteStatus = DebitNoteStatus.Issued,
            PenaltyAmountAtEvent = ndAmount,
            PenaltyCurrencyAtEvent = monId,
            DebitNoteInvoiceId = nd.Id,
        };
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();

        return (reserva, nd, bc);
    }

    private static async Task<decimal> OutstandingAsync(AppDbContext context, int debitNoteId)
    {
        var nd = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == debitNoteId);
        var collected = await context.Payments.AsNoTracking()
            .Where(p => p.LinkedInvoiceId == debitNoteId && p.Status != "Cancelled" && !p.IsDeleted)
            .SumAsync(p => (decimal?)(p.ImputedAmount ?? p.Amount)) ?? 0m;
        var credited = await context.Invoices.AsNoTracking()
            .Where(i => i.OriginalInvoiceId == debitNoteId && i.Resultado == "A"
                     && i.AnnulmentStatus != AnnulmentStatus.Succeeded
                     && (i.TipoComprobante == 3 || i.TipoComprobante == 8 || i.TipoComprobante == 13 || i.TipoComprobante == 53))
            .SumAsync(i => (decimal?)i.ImporteTotal) ?? 0m;
        return nd.ImporteTotal - credited - collected;
    }

    // ============================================================
    // 1) Aplicar contra una multa: happy path
    // ============================================================

    [Fact]
    public async Task Apply_reduces_outstanding_and_creates_bridge_with_correct_flags()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (reserva, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2001", ndAmount: 3000m);

        var service = CreateService(context);
        var result = await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1200m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.False(result.IsReversal);
        Assert.Equal(1200m, result.Amount);
        Assert.Equal(nd.PublicId, result.DebitNotePublicId);
        Assert.Equal(reserva.PublicId, result.TargetReservaPublicId);
        Assert.Equal(3800m, result.AvailableBalanceAfter);

        Assert.Equal(1800m, await OutstandingAsync(context, nd.Id));

        var bridge = await context.Payments.AsNoTracking()
            .SingleAsync(p => p.LinkedInvoiceId == nd.Id && p.Method == AppliedCreditBridge.PenaltyBridgeMethod);
        Assert.Equal(1200m, bridge.Amount);
        Assert.False(bridge.AffectsCash);
        Assert.False(bridge.AffectsReservaBalance);
        Assert.Equal(reserva.Id, bridge.ReservaId);
        Assert.NotNull(bridge.AppliedFromCreditWithdrawalId);
        Assert.True(AppliedCreditBridge.IsPenaltyCreditBridge(bridge));
    }

    [Fact]
    public async Task Apply_drains_multiple_pockets_fifo_against_one_penalty()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        var older = await AddCreditEntryAsync(context, customer.Id, 600m, createdAt: DateTime.UtcNow.AddDays(-2));
        var newer = await AddCreditEntryAsync(context, customer.Id, 800m, createdAt: DateTime.UtcNow.AddDays(-1));
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2002", ndAmount: 1000m);

        var service = CreateService(context);
        var result = await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1000m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(1000m, result.Amount);
        Assert.Equal(older.PublicId, result.EntryPublicId);

        var olderAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.Id == older.Id);
        var newerAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.Id == newer.Id);
        Assert.Equal(0m, olderAfter.RemainingBalance);
        Assert.True(olderAfter.IsFullyConsumed);
        Assert.Equal(400m, newerAfter.RemainingBalance);

        Assert.Equal(0m, await OutstandingAsync(context, nd.Id));
        var bridges = await context.Payments.AsNoTracking()
            .Where(p => p.LinkedInvoiceId == nd.Id && p.Method == AppliedCreditBridge.PenaltyBridgeMethod)
            .ToListAsync();
        Assert.Equal(2, bridges.Count);
    }

    // ============================================================
    // 2) Gates: CAE, moneda, tope, pool, cliente equivocado
    // ============================================================

    [Fact]
    public async Task Apply_when_debit_note_not_issued_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, bc) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2003", ndAmount: 3000m);
        bc.DebitNoteStatus = DebitNoteStatus.Pending; // se está emitiendo todavía, sin CAE vigente.
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1000m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-CLICREDIT-PENALTY-CAE", ex.InvariantCode);
        Assert.Equal(5000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_when_debit_note_not_approved_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2004", ndAmount: 3000m);
        nd.Resultado = "R"; // AFIP rechazo, aunque el flag escalar de la BC siga en Issued.
        await context.SaveChangesAsync();

        var service = CreateService(context);
        // Tanda de saneo (2026-07-22): CancelledDebitNoteCollectionGate ahora tira PaymentValidationException
        // (mensaje de negocio), no InvalidOperationException "a secas". Mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1000m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Contains("aprobada", ex.Message);
    }

    [Fact]
    public async Task Apply_cross_currency_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m, currency: Monedas.ARS);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2005", ndAmount: 3000m, monId: "DOL");

        var service = CreateService(context);
        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1000m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Contains("moneda", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_more_than_outstanding_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2006", ndAmount: 1000m);

        var service = CreateService(context);
        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1500m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Contains("pendiente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_more_than_available_pool_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 500m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2007", ndAmount: 3000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 800m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-085", ex.InvariantCode);
    }

    [Fact]
    public async Task Apply_to_another_customers_penalty_is_rejected()
    {
        await using var context = CreateContext();
        var owner = await AddCustomerAsync(context, "Dueño de la multa");
        var other = await AddCustomerAsync(context, "Otro cliente");
        await AddCreditEntryAsync(context, other.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, owner.Id, "F-2026-2008", ndAmount: 1000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                other.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 500m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-093", ex.InvariantCode);
    }

    [Fact]
    public async Task Apply_twice_in_a_row_second_exceeding_remainder_is_rejected_sequential_double_submit()
    {
        // Simula un doble-submit SECUENCIAL (la protección real de concurrencia vive en la transacción
        // Serializable de Postgres; en InMemory validamos que el pendiente se recalcula fresco en cada
        // llamada, asi que un segundo pedido que ya no entra en lo que queda se rechaza).
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2009", ndAmount: 1000m);

        var service = CreateService(context);
        var first = await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 700m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(700m, first.Amount);
        Assert.Equal(300m, await OutstandingAsync(context, nd.Id));

        // Un segundo pedido IDENTICO (el mismo monto original, 700) ya no entra: solo quedan 300 pendientes.
        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 700m, nd.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Contains("pendiente", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El pendiente sigue en 300 (no se aplico de mas).
        Assert.Equal(300m, await OutstandingAsync(context, nd.Id));
    }

    // ============================================================
    // 3) Reversa
    // ============================================================

    [Fact]
    public async Task Reverse_restores_pocket_and_penalty_outstanding()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2010", ndAmount: 3000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1200m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(1800m, await OutstandingAsync(context, nd.Id));

        var reverse = await service.ReverseCustomerCreditApplicationAsync(
            customer.Id, apply.ApplicationPublicId,
            new ReverseClientCreditApplicationRequest("Aplicacion equivocada"),
            UserId, "Tester", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(5000m, reverse.AvailableBalanceAfter);
        Assert.Equal(3000m, await OutstandingAsync(context, nd.Id));

        var liveBridges = await context.Payments.AsNoTracking()
            .Where(p => p.LinkedInvoiceId == nd.Id && p.Method == AppliedCreditBridge.PenaltyBridgeMethod && !p.IsDeleted)
            .ToListAsync();
        Assert.Empty(liveBridges);
    }

    [Fact]
    public async Task Double_reverse_of_penalty_application_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2011", ndAmount: 3000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1200m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        var reason = new ReverseClientCreditApplicationRequest("Aplicacion equivocada");
        await service.ReverseCustomerCreditApplicationAsync(customer.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ReverseCustomerCreditApplicationAsync(customer.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-098", ex.InvariantCode);
        Assert.Equal(5000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    // ============================================================
    // 4) El puente de multa es invisible en la pestaña Pagos del cliente
    // ============================================================

    [Fact]
    public async Task Penalty_bridge_is_invisible_in_customer_payments_tab()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con multa pagada con saldo");
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2012", ndAmount: 3000m);

        var service = CreateService(context);
        await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1200m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        var customerService = new CustomerService(context, new FinancePositionService(context));
        var page = await customerService.GetCustomerAccountPaymentsAsync(customer.Id, new PagedQuery(), CancellationToken.None);

        Assert.Empty(page.Items);
    }

    // ============================================================
    // 5) Preview de neteo
    // ============================================================

    [Fact]
    public async Task Preview_credit_greater_than_debt_shows_remainder_to_refund()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 10000m);
        await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2013", ndAmount: 3000m);

        var service = CreateService(context);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        Assert.Equal(10000m, preview.AvailableCredit);
        Assert.Equal(3000m, preview.TotalOpenPenalties);
        Assert.Equal(7000m, preview.NetToRefund);
        Assert.Single(preview.OpenPenalties);
        Assert.Contains("7.000", preview.PlainExplanation);
    }

    [Fact]
    public async Task Preview_debt_greater_than_credit_shows_zero_net_and_remaining_debt()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 1000m);
        await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2014", ndAmount: 3000m);

        var service = CreateService(context);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        Assert.Equal(1000m, preview.AvailableCredit);
        Assert.Equal(3000m, preview.TotalOpenPenalties);
        Assert.Equal(0m, preview.NetToRefund);
    }

    [Fact]
    public async Task Preview_without_open_penalties_refunds_everything()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 4000m);

        var service = CreateService(context);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        Assert.Equal(4000m, preview.AvailableCredit);
        Assert.Equal(0m, preview.TotalOpenPenalties);
        Assert.Equal(4000m, preview.NetToRefund);
        Assert.Empty(preview.OpenPenalties);
    }

    // ============================================================
    // 6) Devolucion con neteo (end-to-end)
    // ============================================================

    [Fact]
    public async Task RefundWithNetting_nets_against_penalty_and_refunds_remainder()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 10000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2015", ndAmount: 3000m);

        var service = CreateService(context);
        var result = await service.RefundCustomerCreditWithNettingAsync(
            customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", "Transferencia BBVA"),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(10000m, result.AvailableCreditBefore);
        Assert.Equal(3000m, result.TotalAppliedToPenalties);
        Assert.Equal(7000m, result.NetRefunded);
        Assert.Single(result.PenaltyApplications);
        Assert.NotNull(result.WithdrawalPublicId);
        Assert.Contains("7.000", result.ReceiptText);
        Assert.Contains("Menos multa", result.ReceiptText); // desglose presente

        // El pendiente de la ND quedo en 0 (se pago entera con el saldo a favor).
        Assert.Equal(0m, await OutstandingAsync(context, nd.Id));
        // El pool del cliente en esa moneda quedo en 0 (todo aplicado o devuelto).
        Assert.Equal(0m, await PoolAsync(context, customer.Id, Monedas.ARS));

        // Egreso real de caja creado (ManualCashMovement + CashLedgerEntry), monto = neto.
        var movement = await context.ManualCashMovements.AsNoTracking().SingleAsync();
        Assert.Equal(7000m, movement.Amount);
        var ledgerEntry = await context.CashLedgerEntries.AsNoTracking().SingleAsync();
        Assert.Equal(7000m, ledgerEntry.Amount);
    }

    [Fact]
    public async Task RefundWithNetting_when_credit_fully_consumed_by_penalties_creates_no_egress()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 2000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2016", ndAmount: 3000m);

        var service = CreateService(context);
        var result = await service.RefundCustomerCreditWithNettingAsync(
            customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(2000m, result.TotalAppliedToPenalties);
        Assert.Equal(0m, result.NetRefunded);
        Assert.Null(result.WithdrawalPublicId);

        // Sin egreso: no se creo ningun ManualCashMovement.
        Assert.False(await context.ManualCashMovements.AnyAsync());

        // El pendiente de la ND bajo a 1000 (3000 - 2000 aplicados).
        Assert.Equal(1000m, await OutstandingAsync(context, nd.Id));
    }

    [Fact]
    public async Task RefundWithNetting_without_credit_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RefundCustomerCreditWithNettingAsync(
                customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-CLICREDIT-NOCREDIT", ex.InvariantCode);
    }

    [Fact]
    public async Task RefundWithNetting_ley25345_blocks_large_physical_cash_net()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        // Sin multas: el neto completo es el saldo disponible, mayor al umbral de Ley 25.345 (default 1.000.000).
        await AddCreditEntryAsync(context, customer.Id, amount: 2_000_000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RefundCustomerCreditWithNettingAsync(
                customer.Id, new RefundWithNettingRequest(Monedas.ARS, "PhysicalCash", null),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-094", ex.InvariantCode);

        // Nada se aplico ni se devolvio: el rechazo es previo a cualquier mutacion.
        Assert.Equal(2_000_000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task RefundWithNetting_invalid_method_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 1000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RefundCustomerCreditWithNettingAsync(
                customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Bitcoin", null),
                UserId, "Tester", CancellationToken.None));

        // Gate de exposicion de datos: el mensaje que llega al usuario NO debe nombrar los tokens internos del
        // contrato (PhysicalCash/Transfer) — el controller devuelve ex.Message tal cual (BadRequest).
        Assert.DoesNotContain("PhysicalCash", ex.Message);
        Assert.DoesNotContain("Transfer", ex.Message);
        Assert.Contains("forma de devolución válida", ex.Message);
    }

    // ============================================================
    // 7) ActiveApplications distingue destino (multa vs reserva)
    // ============================================================

    [Fact]
    public async Task ActiveApplications_labels_penalty_destination_with_debit_note_info()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con aplicacion a multa");
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (reserva, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2017", ndAmount: 3000m);

        var service = CreateService(context);
        await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 1200m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        var overview = await service.GetCustomerCreditAsync(customer.Id, CancellationToken.None);
        var line = Assert.Single(overview.ActiveApplications);

        Assert.Equal(ClientCreditApplicationDestinationKind.Penalty, line.DestinationKind);
        Assert.Equal(nd.PublicId, line.DebitNotePublicId);
        Assert.NotNull(line.DebitNoteDisplayNumber);
        Assert.Equal(reserva.PublicId, line.TargetReservaPublicId);
        Assert.Equal(1200m, line.Amount);
    }

    // ============================================================
    // 8) Auditoria
    // ============================================================

    [Fact]
    public async Task Apply_emits_staged_audit_event()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        var (_, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-2018", ndAmount: 1000m);

        var audit = new Mock<IAuditService>();
        var service = CreateService(context, audit);

        await service.ApplyCustomerCreditToPenaltyAsync(
            customer.Id, new ApplyCreditToPenaltyRequest(Monedas.ARS, 500m, nd.PublicId),
            UserId, "Tester", CancellationToken.None);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.ClientCreditAppliedToPenalty,
            AuditActions.ClientCreditWithdrawalEntityName,
            It.IsAny<string>(), It.IsAny<string>(), UserId, It.IsAny<string>()),
            Times.Once);
    }

    // ============================================================
    // 9) Gate de exposicion de datos (fix post-review): los mensajes de error NO deben nombrar llaves internas,
    //    tokens de contrato en ingles, ni nombres de metodo/clase.
    // ============================================================

    [Fact]
    public async Task Apply_when_module_disabled_error_message_does_not_leak_internal_flag_name()
    {
        await using var context = CreateContext();
        var service = CreateService(context, enableNewCancellationFlow: false);

        // No hace falta sembrar cliente/multa: el gate de flag corta ANTES de tocar la base.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyCustomerCreditToPenaltyAsync(
                customerId: 1, new ApplyCreditToPenaltyRequest(Monedas.ARS, 100m, Guid.NewGuid()),
                UserId, "Tester", CancellationToken.None));

        Assert.DoesNotContain("EnableNewCancellationFlow", ex.Message);
        Assert.DoesNotContain("Flow", ex.Message);
    }

    [Fact]
    public async Task RefundWithNetting_when_module_disabled_error_message_does_not_leak_internal_flag_name()
    {
        await using var context = CreateContext();
        var service = CreateService(context, enableNewCancellationFlow: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RefundCustomerCreditWithNettingAsync(
                customerId: 1, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
                UserId, "Tester", CancellationToken.None));

        Assert.DoesNotContain("EnableNewCancellationFlow", ex.Message);
        Assert.DoesNotContain("Flow", ex.Message);
    }

    // ============================================================
    // 10) N2/N3/dedupe (fix post-review): "el neteo descuenta TODO lo que debe el cliente" — sin importar el
    //     vendedor responsable de la reserva, ni el estado de la reserva, y sin doble-contar la misma ND.
    // ============================================================

    [Fact]
    public async Task RefundWithNetting_nets_penalty_from_reserva_owned_by_another_seller()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con multa en reserva de otro vendedor");
        await AddCreditEntryAsync(context, customer.Id, amount: 10000m);
        // La multa vive en una reserva a cargo de OTRO vendedor ("vendedor-ajeno").
        var (_, nd, _) = await SeedIssuedPenaltyAsync(
            context, customer.Id, "F-2026-3001", ndAmount: 3000m, responsibleUserId: "vendedor-ajeno");

        // El vendedor LOGUEADO tiene scope ACOTADO (no ve todas las cobranzas, no es dueño de esa reserva).
        var service = CreateScopedService(context, userId: "vendedor-logueado", seesAllCobranzas: false);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        // Es deuda DEL CLIENTE, no del vendedor: el neteo la descuenta igual (si no, se devuelve de mas).
        Assert.Equal(3000m, preview.TotalOpenPenalties);
        Assert.Equal(7000m, preview.NetToRefund);
        Assert.Single(preview.OpenPenalties);

        var result = await service.RefundCustomerCreditWithNettingAsync(
            customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(3000m, result.TotalAppliedToPenalties);
        Assert.Equal(0m, await OutstandingAsync(context, nd.Id));
    }

    [Fact]
    public async Task RefundWithNetting_nets_penalty_from_partial_cancellation_on_still_active_reserva()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con anulacion parcial sobre reserva viva");
        await AddCreditEntryAsync(context, customer.Id, amount: 5000m);
        // La reserva sigue VIVA (Confirmed, no Cancelled): una cancelacion PARCIAL (ADR-025) dejo una ND firme
        // sobre UN servicio, mientras el resto de la reserva sigue en curso.
        var (_, nd, _) = await SeedIssuedPenaltyAsync(
            context, customer.Id, "F-2026-3002", ndAmount: 1500m, reservaStatus: EstadoReserva.Confirmed);

        var service = CreateService(context);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        Assert.Equal(1500m, preview.TotalOpenPenalties);
        Assert.Single(preview.OpenPenalties);

        var result = await service.RefundCustomerCreditWithNettingAsync(
            customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(1500m, result.TotalAppliedToPenalties);
        Assert.Equal(0m, await OutstandingAsync(context, nd.Id));
    }

    [Fact]
    public async Task RefundWithNetting_dedupes_two_bookingcancellations_pointing_to_same_debit_note()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con dos BCs sobre la misma ND");
        await AddCreditEntryAsync(context, customer.Id, amount: 10000m);
        var (reserva, nd, _) = await SeedIssuedPenaltyAsync(context, customer.Id, "F-2026-3003", ndAmount: 3000m);

        // Segunda BC (ej.: BC hija de una cancelacion multi-operador) que apunta a la MISMA Nota de Debito.
        context.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id,
            Reason = "Segundo operador de la misma cancelacion",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyStatus = PenaltyStatus.Confirmed,
            DebitNoteStatus = DebitNoteStatus.Issued,
            PenaltyAmountAtEvent = 3000m,
            PenaltyCurrencyAtEvent = "PES",
            DebitNoteInvoiceId = nd.Id,
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var preview = await service.GetCustomerRefundNettingPreviewAsync(customer.Id, Monedas.ARS, CancellationToken.None);

        // Sin dedupe, la ND apareceria DOS veces en el listado y el neteo la aplicaria dos veces.
        Assert.Single(preview.OpenPenalties);
        Assert.Equal(3000m, preview.TotalOpenPenalties);

        var result = await service.RefundCustomerCreditWithNettingAsync(
            customer.Id, new RefundWithNettingRequest(Monedas.ARS, "Transfer", null),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(3000m, result.TotalAppliedToPenalties);
        Assert.Single(result.PenaltyApplications);
        Assert.Equal(0m, await OutstandingAsync(context, nd.Id));
        // Sin dedupe, la ND se hubiera descontado DOS VECES (6000 en vez de 3000) y el neto devuelto hubiera
        // sido 4000 en lugar de 7000. El pool completo (10000) queda en 0: 3000 a la multa + 7000 de egreso real.
        Assert.Equal(7000m, result.NetRefunded);
        Assert.Equal(0m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    private static async Task<decimal> PoolAsync(AppDbContext context, int customerId, string currency)
    {
        var rows = await context.ClientCreditEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId)
            .Select(e => new { e.Currency, e.RemainingBalance }).ToListAsync();
        return rows.Where(r => Monedas.Normalizar(r.Currency) == currency).Sum(r => r.RemainingBalance);
    }
}
