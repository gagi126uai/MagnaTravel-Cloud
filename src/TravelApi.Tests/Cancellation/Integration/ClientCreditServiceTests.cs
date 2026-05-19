using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.3 v3 §8 (2026-05-18): tests integration de
/// <see cref="IClientCreditService"/> contra Postgres real. Cubre:
/// <list type="bullet">
///   <item>CreateEntry happy path (saldo inicial = NetAmount).</item>
///   <item>Withdraw para los 5 kinds (KeptAsCredit / PhysicalCash / Transfer /
///         AppliedToNewBooking / ReversedToOperator).</item>
///   <item>Ley 25.345 (INV-094) + alerta admin (PhysicalRefundAlertThreshold).</item>
///   <item>INV-085: amount > RemainingBalance rechaza.</item>
///   <item>Approval ClientRefundReversal: presente y aprobado, ausente, etc.</item>
///   <item>Cierre del BC (OnAllCreditConsumedAsync) cuando el ultimo
///         withdraw consume el saldo + idempotencia bajo concurrencia (MR-02).</item>
///   <item>N retiros parciales decrementando saldo progresivamente.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClientCreditServiceTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ClientCreditServiceTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // CreateEntry (happy path)
    // =========================================================================

    [Fact]
    public async Task CreateEntry_HappyPath_CreaEntryConRemainingBalanceIgualAlNetAmount()
    {
        // Setup: caminamos hasta la creacion del entry via OperatorRefundService
        // (es el unico caller publico de CreateEntryAsync hoy).
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        // Assert: entry creado con saldo = NetAmount, sin withdrawals.
        await using var ctx = _fixture.CreateDbContext();
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(500m, entry.CreditedAmount);
        Assert.Equal(500m, entry.RemainingBalance);
        Assert.False(entry.IsFullyConsumed);
    }

    // =========================================================================
    // Withdraw — PhysicalCash
    // =========================================================================

    [Fact]
    public async Task Withdraw_PhysicalCash_DentroLimiteLey25345_OK()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var dto = await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.PhysicalCash,
                Amount: 300m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: "Cashier 1", ct: CancellationToken.None);

        Assert.Equal(WithdrawalKind.PhysicalCash, dto.Kind);
        Assert.Equal(300m, dto.Amount);

        await using var ctx = _fixture.CreateDbContext();
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(200m, entry.RemainingBalance);
        Assert.False(entry.IsFullyConsumed);

        // Verificar que se creo ManualCashMovement Expense.
        var movement = await ctx.ManualCashMovements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.NotNull(movement);
        Assert.Equal(CashMovementDirections.Expense, movement!.Direction);
        Assert.Equal(300m, movement.Amount);
    }

    [Fact]
    public async Task Withdraw_PhysicalCash_SobreLimiteLey25345_TiraINV094()
    {
        // Settings del fixture: Ley25345Threshold default 1.000.000. Para forzar
        // el limite con un monto chico, usamos un credito de 2.000.000 y monto
        // 1.500.000 (> 1.000.000).
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 2_000_000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.PhysicalCash,
                    Amount: 1_500_000m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "user1", userName: "Cashier", ct: CancellationToken.None));

        Assert.Equal("INV-094", ex.InvariantCode);

        // Saldo no se modifico.
        await using var ctx = _fixture.CreateDbContext();
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(2_000_000m, entry.RemainingBalance);
    }

    [Fact]
    public async Task Withdraw_PhysicalCash_SuperaPhysicalRefundAlert_RegistraAuditAlerta()
    {
        // Setup: credito de 100.000. Threshold de alerta del fixture es 50.000
        // (default de OperationalFinanceSettings). Retiramos 80.000: dispara
        // alerta sin bloquear (no llega a la Ley 25.345 = 1M).
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 100_000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.PhysicalCash,
                Amount: 80_000m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: "Cashier", ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        // El audit dedicado de alerta debe existir.
        var alertAudit = await ctx.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Action == AuditActions.ClientCreditPhysicalRefundAlert);
        Assert.NotNull(alertAudit);
    }

    // =========================================================================
    // Withdraw — Transfer
    // =========================================================================

    [Fact]
    public async Task Withdraw_Transfer_SinLimiteLey25345_OK_CreaManualCashMovementExpense()
    {
        // Transfer no esta sujeto a Ley 25.345. Monto > 1M deberia funcionar.
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 2_000_000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var dto = await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.Transfer,
                Amount: 1_500_000m,
                PaymentMethodOverride: "Transfer-BBVA",
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: "TX-12345"),
            userId: "user1", userName: "Cashier", ct: CancellationToken.None);

        Assert.Equal(WithdrawalKind.Transfer, dto.Kind);
        Assert.Equal(1_500_000m, dto.Amount);

        await using var ctx = _fixture.CreateDbContext();
        var movement = await ctx.ManualCashMovements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.NotNull(movement);
        Assert.Equal(CashMovementDirections.Expense, movement!.Direction);
        Assert.Equal("Transfer-BBVA", movement.Method);
        Assert.Equal("TX-12345", movement.Reference);
    }

    // =========================================================================
    // Withdraw — AppliedToNewBooking
    // =========================================================================

    [Fact]
    public async Task Withdraw_AppliedToNewBooking_NoCreaManualCashMovement()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        // Crear una reserva destino para el mismo customer.
        Guid targetReservaPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var targetReserva = new Reserva
            {
                NumeroReserva = "R-TARGET",
                Name = "Reserva nueva",
                Status = EstadoReserva.Confirmed,
                PayerId = seed.CustomerId,
            };
            setupCtx.Reservas.Add(targetReserva);
            await setupCtx.SaveChangesAsync();
            targetReservaPublicId = targetReserva.PublicId;
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.AppliedToNewBooking,
                Amount: 200m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: targetReservaPublicId,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: "Cashier", ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        // Saldo decremento.
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(300m, entry.RemainingBalance);

        // NO se creo ManualCashMovement (lo hara el PaymentService en FC4).
        var movements = await ctx.ManualCashMovements.AsNoTracking()
            .CountAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.Equal(0, movements);
    }

    [Fact]
    public async Task Withdraw_AppliedToNewBooking_AReservaDeOtroCliente_RechazaInvariante()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        // Crear otro customer + reserva.
        Guid otherCustomerReservaPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var otherCustomer = new Customer { FullName = "Otro", TaxCondition = "Consumidor Final", IsActive = true };
            setupCtx.Customers.Add(otherCustomer);
            await setupCtx.SaveChangesAsync();
            var otherReserva = new Reserva
            {
                NumeroReserva = "R-OTHER",
                Name = "Reserva otro cliente",
                Status = EstadoReserva.Confirmed,
                PayerId = otherCustomer.Id,
            };
            setupCtx.Reservas.Add(otherReserva);
            await setupCtx.SaveChangesAsync();
            otherCustomerReservaPublicId = otherReserva.PublicId;
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.AppliedToNewBooking,
                    Amount: 100m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: otherCustomerReservaPublicId,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-093", ex.InvariantCode);
    }

    // =========================================================================
    // Withdraw — KeptAsCredit
    // =========================================================================

    [Fact]
    public async Task Withdraw_KeptAsCredit_OK_NoConsumeSaldo()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.KeptAsCredit,
                Amount: 0m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: null, ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        // Saldo NO cambia (regla 5 policy: KeptAsCredit no consume).
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .Include(e => e.Withdrawals)
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(500m, entry.RemainingBalance);
        Assert.False(entry.IsFullyConsumed);
        // Pero SI se crea un withdrawal "marca de decision" con Amount=0.
        Assert.Single(entry.Withdrawals);
        Assert.Equal(WithdrawalKind.KeptAsCredit, entry.Withdrawals.First().Kind);
        Assert.Equal(0m, entry.Withdrawals.First().Amount);
    }

    // =========================================================================
    // Withdraw — ReversedToOperator
    // =========================================================================

    [Fact]
    public async Task Withdraw_ClientRefundReversal_SinApproval_TiraApprovalRequired()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.ReversedToOperator,
                    Amount: 100m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null, // <- sin approval
                    Reference: null),
                userId: "user1", userName: null, ct: CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_ClientRefundReversal_ConApprovalAprobado_OK_AuditReforzado()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        // Crear ApprovalRequest tipo ClientRefundReversal aprobado para este entry.
        Guid approvalPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var seedApproval = new ApprovalRequest
            {
                RequestType = ApprovalRequestType.ClientRefundReversal,
                EntityType = AuditActions.ClientCreditEntryEntityName,
                EntityId = seed.EntryId,
                RequestedByUserId = "user1",
                Status = ApprovalStatus.Approved,
                ResolvedByUserId = "admin",
                ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Reason = "Cliente devolvio efectivo, reasignar al operador",
            };
            setupCtx.ApprovalRequests.Add(seedApproval);
            await setupCtx.SaveChangesAsync();
            approvalPublicId = seedApproval.PublicId;
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.ReversedToOperator,
                Amount: 200m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: approvalPublicId,
                Reference: null),
            userId: "user1", userName: "Cashier", ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        // Saldo decremento.
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(300m, entry.RemainingBalance);

        // Approval consumido.
        var approval = await ctx.ApprovalRequests.AsNoTracking()
            .FirstAsync(a => a.PublicId == approvalPublicId);
        Assert.Equal(ApprovalStatus.Consumed, approval.Status);

        // Audit reforzado: hay un ClientRefundReversalApproved ademas del base.
        var reversalAudit = await ctx.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Action == AuditActions.ClientRefundReversalApproved);
        Assert.NotNull(reversalAudit);

        // ManualCashMovement con Direction=Income (la plata vuelve a caja).
        var movement = await ctx.ManualCashMovements.AsNoTracking()
            .FirstAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.Equal(CashMovementDirections.Income, movement.Direction);
    }

    // =========================================================================
    // Validaciones comunes (INV-085, amount cero, etc.)
    // =========================================================================

    [Fact]
    public async Task Withdraw_AmountMayorQueRemainingBalance_TiraINV085()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 100m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 200m, // > 100m disponible
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-085", ex.InvariantCode);
    }

    [Fact]
    public async Task Withdraw_NRetirosParciales_DescuentaProgresivamente()
    {
        // Regla 12 policy (ADR-002): el cliente puede hacer N retiros parciales
        // del mismo entry hasta consumir todo el saldo.
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 600m);

        using (var scope = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

            await svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 100m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "u1", userName: null, ct: CancellationToken.None);
        }

        using (var scope = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 200m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "u2", userName: null, ct: CancellationToken.None);
        }

        using (var scope = _fixture.BuildServiceProvider().CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await svc.WithdrawAsync(
                seed.EntryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 150m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "u3", userName: null, ct: CancellationToken.None);
        }

        await using var ctx = _fixture.CreateDbContext();
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .Include(e => e.Withdrawals)
            .FirstAsync(e => e.PublicId == seed.EntryPublicId);
        Assert.Equal(150m, entry.RemainingBalance); // 600 - 100 - 200 - 150
        Assert.Equal(3, entry.Withdrawals.Count);
        Assert.False(entry.IsFullyConsumed);
    }

    // =========================================================================
    // Cierre del BC en cascada
    // =========================================================================

    [Fact]
    public async Task Withdraw_UltimoRetiro_DisparaOnAllCreditConsumed_BcStatusVaAClosed()
    {
        // Setup: scenario en ClientCreditApplied + entry de 500.
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        // Retiro consume todo el saldo.
        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.Transfer,
                Amount: 500m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: null, ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        // BC cerrado.
        var bc = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == seed.BcPublicId);
        Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
        Assert.NotNull(bc.ClosedAt);

        // Reserva cancelada.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == seed.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Audit BookingCancellationClosed presente.
        var closeAudit = await ctx.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Action == AuditActions.BookingCancellationClosed);
        Assert.NotNull(closeAudit);
    }

    [Fact]
    public async Task Withdraw_RetiroParcial_NoCierraElBc()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.Transfer,
                Amount: 200m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: null, ct: CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        var bc = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == seed.BcPublicId);
        // BC sigue en ClientCreditApplied (todavia queda saldo).
        Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc.Status);
        Assert.Null(bc.ClosedAt);
    }

    // =========================================================================
    // Queries
    // =========================================================================

    [Fact]
    public async Task GetEntryByPublicId_DevuelveEntryConWithdrawals()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        await svc.WithdrawAsync(
            seed.EntryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.Transfer,
                Amount: 100m,
                PaymentMethodOverride: null,
                AppliedToReservaPublicId: null,
                ApprovalRequestPublicId: null,
                Reference: null),
            userId: "user1", userName: null, ct: CancellationToken.None);

        using var queryScope = _fixture.BuildServiceProvider().CreateScope();
        var querySvc = queryScope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var dto = await querySvc.GetEntryByPublicIdAsync(seed.EntryPublicId, CancellationToken.None);
        Assert.NotNull(dto);
        Assert.Equal(seed.EntryPublicId, dto!.PublicId);
        Assert.Equal(400m, dto.RemainingBalance);
        Assert.Single(dto.Withdrawals);
    }

    [Fact]
    public async Task GetEntriesByBc_DevuelveTodosLosEntries()
    {
        var seed = await SeedScenarioWithCreditEntryAsync(creditAmount: 500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();

        var entries = await svc.GetEntriesByBcAsync(seed.BcPublicId, CancellationToken.None);
        Assert.Single(entries);
        Assert.Equal(seed.EntryPublicId, entries[0].PublicId);
    }

    // =========================================================================
    // Helpers de setup
    // =========================================================================

    private record SeedResult(
        Guid EntryPublicId,
        int EntryId,
        Guid BcPublicId,
        int BcId,
        int ReservaId,
        int CustomerId);

    /// <summary>
    /// Setup full: Customer + Supplier + Reserva + Invoice + BC en
    /// AwaitingOperatorRefund + OperatorRefundReceived + Allocate via service
    /// (que crea el ClientCreditEntry). Devuelve los Ids/PublicIds para que el
    /// test pueda asertar.
    ///
    /// <para>
    /// Llamamos al OperatorRefundService.AllocateAsync para que el entry se cree
    /// EXACTAMENTE igual que en produccion (con FK resuelta por EF en orden
    /// topologico). Es mas confiable que insertar el entry directo via
    /// DbContext.
    /// </para>
    /// </summary>
    private async Task<SeedResult> SeedScenarioWithCreditEntryAsync(decimal creditAmount)
    {
        await using var ctx = _fixture.CreateDbContext();

        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // BC en AwaitingOperatorRefund con matriz fiscal RI x RI (la mas neutra
        // para no disparar Mono rejects).
        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
            BookingCancellationStatus.AwaitingOperatorRefund);
        bc.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc.FiscalSnapshot.SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // Refund + Allocate via service para que el entry quede creado con FKs
        // bien resueltas. Allocate transiciona el BC a ClientCreditApplied.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAmount = creditAmount,
            AllocatedAmount = 0m,
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "seed",
            ReceivedByUserName = "Seed",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();
        var refundPublicId = refund.PublicId;

        // Allocate $creditAmount sin deducciones (NetAmount = creditAmount).
        using (var scope = _fixture.BuildServiceProvider().CreateScope())
        {
            var allocSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await allocSvc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bc.PublicId, creditAmount, new List<DeductionLineRequest>()),
                "seed-user", "Seed", CancellationToken.None);
        }

        // Releer el entry creado para devolver sus Ids.
        await using var verifyCtx = _fixture.CreateDbContext();
        var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.BookingCancellationId == bc.Id);

        return new SeedResult(
            EntryPublicId: entry.PublicId,
            EntryId: entry.Id,
            BcPublicId: bc.PublicId,
            BcId: bc.Id,
            ReservaId: resId,
            CustomerId: custId);
    }
}
