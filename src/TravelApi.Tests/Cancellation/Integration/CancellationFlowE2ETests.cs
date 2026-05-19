using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.7a v3 §9 (2026-05-18): tests end-to-end del flujo completo de
/// cancelacion + refund + credito al cliente, contra Postgres real
/// (<see cref="PostgresIntegrationFixture"/>).
///
/// <para>
/// <b>Que es un test E2E aca</b>: recorre TODO el flujo (Draft → Confirm →
/// callback AFIP → RecordReceived → Allocate → Withdraw → Closed) invocando
/// los services directamente (NO via HTTP). La diferencia con los tests
/// unitarios de cada service es que aca validamos la coordinacion entre los
/// 3 services (<see cref="IBookingCancellationService"/>,
/// <see cref="IOperatorRefundService"/>, <see cref="IClientCreditService"/>)
/// + el bridge AFIP simulado.
/// </para>
///
/// <para>
/// <b>Estrategia para AFIP</b>: el fixture registra <see cref="IInvoiceService"/>
/// como mock no-op (no encola jobs reales de Hangfire ni llama AFIP). Despues
/// del <c>ConfirmAsync</c> simulamos "AFIP respondio OK" creando manualmente
/// la NC (Invoice tipo 3/8/13 con CAE) y llamando al bridge
/// (<see cref="IInvoiceAnnulmentBcBridge.OnArcaSucceededAsync"/>) que en
/// produccion lo invocaria el job ProcessAnnulmentJob al recibir el CAE real.
/// Esto cierra la cadena BC ↔ Invoice sin depender de Hangfire ni AFIP.
/// </para>
///
/// <para>
/// <b>Por que no mockear</b>: tests E2E que mockean el DbContext son inutiles
/// — el modulo se apoya en CHECK constraints SQL (INV-084/085/100/112/118) y en
/// el concurrency token xmin de Postgres. Si esos no estan, el test pasa pero
/// produccion rompe.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CancellationFlowE2ETests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public CancellationFlowE2ETests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Happy path: cancelacion completa con devolucion total al cliente
    // =========================================================================

    /// <summary>
    /// Happy path canonico:
    /// <list type="number">
    ///   <item>Vendedor: Draft de la cancelacion.</item>
    ///   <item>Vendedor: Confirm (encola NC en AFIP via mock + transiciona BC
    ///         a AwaitingFiscalConfirmation + Reserva a PendingOperatorRefund).</item>
    ///   <item>Sistema: simular "AFIP devolvio CAE" creando NC + bridge
    ///         OnArcaSucceededAsync → BC pasa a AwaitingOperatorRefund.</item>
    ///   <item>Cajero: RecordReceived del refund del operador.</item>
    ///   <item>Cajero: Allocate del refund contra el BC (sin deducciones) →
    ///         crea ClientCreditEntry + BC pasa a ClientCreditApplied.</item>
    ///   <item>Cajero: Withdraw Transfer del saldo completo → BC pasa a Closed,
    ///         Reserva pasa a Cancelled, ClientCreditEntry queda totalmente
    ///         consumido.</item>
    /// </list>
    /// Validamos cada transicion de estado y cada audit log dedicado.
    /// </summary>
    [Fact]
    public async Task HappyPath_FlujoCompletoConTransferAlCliente_CierraBcYCancelaReserva()
    {
        // Setup: escenario base con Invoice tipo A + Reserva confirmada.
        var seed = await SeedE2EScenarioAsync();

        var provider = _fixture.BuildServiceProvider();

        // ---------- Paso 1 + 2: Draft + Confirm ----------
        Guid bcPublicId;
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();

            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Cliente cambio fecha"),
                userId: "user-vendor", userName: "Vendedor", ct: CancellationToken.None);

            bcPublicId = draft.PublicId;
            Assert.Equal("Drafted", draft.Status);

            var confirmed = await bcSvc.ConfirmAsync(
                draft.PublicId,
                BuildValidConfirm(),
                userId: "user-vendor", userName: "Vendedor",
                requesterIsAdmin: false, ct: CancellationToken.None);

            Assert.Equal("AwaitingFiscalConfirmation", confirmed.Status);
        }

        // Verificamos que la Reserva quedo PendingOperatorRefund (el BC service
        // marca la transicion bypass UpdateStatusAsync, ver step 8 del Confirm).
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var reserva = await verifyCtx.Reservas.AsNoTracking()
                .FirstAsync(r => r.Id == seed.ReservaId);
            Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);
        }

        // ---------- Paso 3: simular AFIP OK via bridge ----------
        // En produccion lo invoca ProcessAnnulmentJob (Hangfire) cuando AFIP
        // devuelve CAE. Aca lo invocamos manualmente con la NC creada a mano.
        int creditNoteInvoiceId = await CreateNcInvoiceAsync(
            seed.ReservaId, originalInvoiceId: seed.InvoiceId,
            tipoNc: 3, // NC A (porque la original es Factura A tipo 1)
            numeroComprobante: 2);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(
                originatingInvoiceId: seed.InvoiceId,
                creditNoteInvoiceId: creditNoteInvoiceId,
                ct: CancellationToken.None);
        }

        // El BC debe estar en AwaitingOperatorRefund + Reserva en PendingOperatorRefund.
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var bc = await verifyCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
            Assert.Equal(creditNoteInvoiceId, bc.CreditNoteInvoiceId);
            Assert.Null(bc.ArcaConfirmedManuallyAt); // callback automatico, no manual
        }

        // ---------- Paso 4: RecordReceived ----------
        Guid refundPublicId;
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refundDto = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    SupplierPublicId: seed.SupplierPublicId,
                    ReceivedAmount: 5_000m, // chico para no chocar con Ley 25.345
                    Currency: "ARS",
                    ReceivedAt: DateTime.UtcNow,
                    Method: "Transfer",
                    Reference: "OP-REFUND-001",
                    Notes: "Devolucion operador happy path"),
                userId: "user-cashier", userName: "Cajero",
                ct: CancellationToken.None);
            refundPublicId = refundDto.PublicId;
        }

        // ---------- Paso 5: Allocate sin deducciones (NetAmount = 5000) ----------
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var allocation = await refundSvc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, 5_000m, new List<DeductionLineRequest>()),
                userId: "user-cashier", userName: "Cajero",
                ct: CancellationToken.None);

            Assert.Equal(5_000m, allocation.GrossAmount);
            Assert.Equal(5_000m, allocation.NetAmount);
            Assert.False(allocation.IsVoided);
        }

        // BC ahora debe estar en ClientCreditApplied + ClientCreditEntry creado
        // con saldo 5000.
        Guid entryPublicId;
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var bc = await verifyCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc.Status);

            var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
                .FirstAsync(e => e.BookingCancellationId == bc.Id);
            Assert.Equal(5_000m, entry.CreditedAmount);
            Assert.Equal(5_000m, entry.RemainingBalance);
            entryPublicId = entry.PublicId;
        }

        // ---------- Paso 6: Withdraw Transfer del saldo completo ----------
        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 5_000m,
                    PaymentMethodOverride: "Transfer-BBVA",
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: "TX-HAPPY-001"),
                userId: "user-cashier", userName: "Cajero",
                ct: CancellationToken.None);
        }

        // ---------- Asserts finales ----------
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            // BC en Closed + Reserva en Cancelled.
            var bc = await verifyCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
            Assert.NotNull(bc.ClosedAt);

            var reserva = await verifyCtx.Reservas.AsNoTracking()
                .FirstAsync(r => r.Id == seed.ReservaId);
            Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

            // ClientCreditEntry totalmente consumido.
            var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
                .Include(e => e.Withdrawals)
                .FirstAsync(e => e.PublicId == entryPublicId);
            Assert.Equal(0m, entry.RemainingBalance);
            Assert.True(entry.IsFullyConsumed);
            Assert.Single(entry.Withdrawals);

            // ManualCashMovement de egreso por el Withdraw.
            var withdrawMovement = await verifyCtx.ManualCashMovements.AsNoTracking()
                .FirstAsync(m => m.ClientCreditWithdrawalId != null);
            Assert.Equal(CashMovementDirections.Expense, withdrawMovement.Direction);
            Assert.Equal(5_000m, withdrawMovement.Amount);
            Assert.Equal("Transfer-BBVA", withdrawMovement.Method);

            // ManualCashMovement Income por el refund ingresado.
            var refundMovement = await verifyCtx.ManualCashMovements.AsNoTracking()
                .FirstAsync(m => m.OperatorRefundReceivedId != null);
            Assert.Equal(CashMovementDirections.Income, refundMovement.Direction);
            Assert.Equal(5_000m, refundMovement.Amount);

            // Allocation NetAmount == Entry CreditedAmount (regla 3 policy).
            var allocation = await verifyCtx.OperatorRefundAllocations.AsNoTracking()
                .FirstAsync(a => a.BookingCancellationId == bc.Id);
            Assert.Equal(entry.CreditedAmount, allocation.NetAmount);

            // Audit logs criticos: cada accion debe haber dejado huella.
            var expectedActions = new[]
            {
                AuditActions.BookingCancellationDrafted,
                AuditActions.BookingCancellationConfirmed,
                AuditActions.BookingCancellationArcaSucceeded,
                AuditActions.OperatorRefundReceivedRegistered,
                AuditActions.OperatorRefundAllocated,
                AuditActions.ClientCreditWithdrawn,
                AuditActions.BookingCancellationClosed,
            };
            var auditActions = await verifyCtx.AuditLogs.AsNoTracking()
                .Select(a => a.Action)
                .ToListAsync();
            foreach (var expected in expectedActions)
            {
                Assert.Contains(expected, auditActions);
            }
        }
    }

    // =========================================================================
    // Variante 1: Factura B (consumidor final) + NC tipo B
    // =========================================================================

    /// <summary>
    /// Variante 1: la reserva tiene Factura B (TipoComprobante=6, tipica de
    /// consumidor final) en vez de Factura A. El flujo debe correr completo
    /// igual y el sistema debe tolerar la NC tipo 8 (NC B).
    ///
    /// <para>
    /// <b>Nota implementacion</b>: el BC service NO bifurca por tipo de factura
    /// (TipoComprobante en {1, 6, 11} todos siguen el mismo flujo). El bridge
    /// `OnArcaSucceededAsync` confia en que el job `ProcessAnnulmentJob` ya
    /// valido el tipo de NC antes de invocarlo — el bridge en si solo matchea
    /// por OriginatingInvoiceId + Status, NO valida tipo NC ni coherencia
    /// con la factura original (queda como deuda tecnica, ver BR-V2-XX backlog).
    /// Aca usamos B/NCB para confirmar que el path no esta hardcodeado a tipo A.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Variante1_FacturaB_FlujoCompleto_CierraOK()
    {
        var seed = await SeedE2EScenarioAsync(invoiceTipoComprobante: 6);

        var provider = _fixture.BuildServiceProvider();

        Guid bcPublicId;
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Cliente consumidor final cancela"),
                "user-vendor", "Vendedor", CancellationToken.None);
            bcPublicId = draft.PublicId;

            await bcSvc.ConfirmAsync(
                draft.PublicId,
                BuildValidConfirm(customerCondition: "Consumidor Final"),
                "user-vendor", "Vendedor",
                requesterIsAdmin: false, ct: CancellationToken.None);
        }

        // Simular AFIP OK con NC tipo 8 (NC B).
        int creditNoteId = await CreateNcInvoiceAsync(
            seed.ReservaId, originalInvoiceId: seed.InvoiceId,
            tipoNc: 8, // NC B
            numeroComprobante: 2);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(seed.InvoiceId, creditNoteId, CancellationToken.None);
        }

        // RecordReceived + Allocate + Withdraw (mismo flujo que el happy path).
        Guid refundPublicId;
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refundDto = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, 3_000m, "ARS", DateTime.UtcNow,
                    "Transfer", "OP-V1", null),
                "user-cashier", null, CancellationToken.None);
            refundPublicId = refundDto.PublicId;

            await refundSvc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, 3_000m, new List<DeductionLineRequest>()),
                "user-cashier", null, CancellationToken.None);
        }

        Guid entryPublicId;
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
                .FirstAsync(e => e.BookingCancellationId
                    == verifyCtx.BookingCancellations.First(b => b.PublicId == bcPublicId).Id);
            entryPublicId = entry.PublicId;
        }

        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    WithdrawalKind.Transfer, 3_000m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                "user-cashier", null, CancellationToken.None);
        }

        // BC cerrado + NC linkeada con tipo 8.
        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var bc = await verifyCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
            Assert.Equal(creditNoteId, bc.CreditNoteInvoiceId);

            var nc = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == creditNoteId);
            Assert.Equal(8, nc.TipoComprobante);
        }
    }

    // =========================================================================
    // Variante 2: Abort del draft (cancelar la cancelacion)
    // =========================================================================

    /// <summary>
    /// Variante 2: el vendedor crea el draft pero el cliente se arrepiente
    /// antes de Confirm. AbortAsync deja el BC en Aborted y la Reserva sigue
    /// activa (no se toco fiscalmente).
    /// </summary>
    [Fact]
    public async Task Variante2_AbortDelDraft_ReservaQuedaActivaBcAborted()
    {
        var seed = await SeedE2EScenarioAsync();

        var provider = _fixture.BuildServiceProvider();
        Guid bcPublicId;

        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Vendedor se equivoco al cargar"),
                "user-vendor", "Vendor", CancellationToken.None);
            bcPublicId = draft.PublicId;

            await bcSvc.AbortAsync(
                draft.PublicId, "Cliente se arrepintio", "user-vendor", CancellationToken.None);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.Aborted, bc.Status);
        Assert.NotNull(bc.ClosedAt);

        // Reserva sigue Confirmed (estado inicial del seed): el Abort no toca
        // la Reserva porque todavia no se habia hecho la transicion fiscal.
        var reserva = await verifyCtx.Reservas.AsNoTracking()
            .FirstAsync(r => r.Id == seed.ReservaId);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);

        // No deberia existir ningun OperatorRefund, ClientCreditEntry, ni NC.
        Assert.False(await verifyCtx.OperatorRefundReceived.AsNoTracking().AnyAsync());
        Assert.False(await verifyCtx.ClientCreditEntries.AsNoTracking().AnyAsync());
        // El audit del Abort debe existir.
        Assert.True(await verifyCtx.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.BookingCancellationAborted));
    }

    // =========================================================================
    // Variante 3: ReversedToOperator (cliente devuelve plata cobrada)
    // =========================================================================

    /// <summary>
    /// Variante 3: el cliente ya retiro la plata pero la quiere devolver al
    /// operador (caso atipico, requiere approval reforzado).
    ///
    /// <para>
    /// Flujo: happy path completo hasta llegar al ClientCreditEntry. El cajero
    /// crea un ApprovalRequest <see cref="ApprovalRequestType.ClientRefundReversal"/>
    /// aprobado y hace Withdraw con <see cref="WithdrawalKind.ReversedToOperator"/>.
    /// El sistema debe registrar audit reforzado
    /// (<see cref="AuditActions.ClientRefundReversalApproved"/>) y crear un
    /// ManualCashMovement con Direction=Income (la plata vuelve a caja).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Variante3_ReversedToOperator_ConApprovalAprobado_AuditReforzadoYMovementIncome()
    {
        var seed = await SeedE2EScenarioAsync();

        var provider = _fixture.BuildServiceProvider();

        var (bcPublicId, entryPublicId, entryId, _) = await RunFlowUntilCreditEntryAsync(
            seed, provider, creditAmount: 1_000m);

        // Crear ApprovalRequest tipo ClientRefundReversal aprobado.
        Guid approvalPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var approval = new ApprovalRequest
            {
                RequestType = ApprovalRequestType.ClientRefundReversal,
                EntityType = AuditActions.ClientCreditEntryEntityName,
                EntityId = entryId,
                RequestedByUserId = "user-cashier",
                RequestedAt = DateTime.UtcNow,
                Status = ApprovalStatus.Approved,
                ResolvedByUserId = "admin",
                ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Reason = "Cliente devuelve efectivo, reasignar al operador",
            };
            setupCtx.ApprovalRequests.Add(approval);
            await setupCtx.SaveChangesAsync();
            approvalPublicId = approval.PublicId;
        }

        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.ReversedToOperator,
                    Amount: 1_000m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: approvalPublicId,
                    Reference: null),
                userId: "user-cashier", userName: "Cajero",
                ct: CancellationToken.None);
        }

        await using var verifyCtx = _fixture.CreateDbContext();

        // Approval consumido. Renombramos a approvalAfter porque el approval
        // creado arriba en el bloque setupCtx sigue en scope.
        var approvalAfter = await verifyCtx.ApprovalRequests.AsNoTracking()
            .FirstAsync(a => a.PublicId == approvalPublicId);
        Assert.Equal(ApprovalStatus.Consumed, approvalAfter.Status);

        // Audit reforzado: ClientRefundReversalApproved + ClientCreditWithdrawn.
        Assert.True(await verifyCtx.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.ClientRefundReversalApproved));
        Assert.True(await verifyCtx.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.ClientCreditWithdrawn));

        // ManualCashMovement Income (la plata vuelve a caja).
        var movement = await verifyCtx.ManualCashMovements.AsNoTracking()
            .FirstAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.Equal(CashMovementDirections.Income, movement.Direction);
        Assert.Equal(1_000m, movement.Amount);

        // BC cerrado (consumo total).
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
    }

    // =========================================================================
    // Variante 4: AppliedToNewBooking (saldo se aplica a otra reserva)
    // =========================================================================

    /// <summary>
    /// Variante 4: el cliente decide usar el saldo como pago de una nueva
    /// reserva (mismo customer). El sistema debe:
    /// <list type="bullet">
    ///   <item>Crear el withdrawal con kind <see cref="WithdrawalKind.AppliedToNewBooking"/>.</item>
    ///   <item>NO crear <c>ManualCashMovement</c> (lo hara el PaymentService al
    ///         registrar el pago en la nueva reserva — fuera de FC1).</item>
    ///   <item>Cerrar el BC (consumo total del saldo).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Variante4_AppliedToNewBooking_NoCreaCashMovementYBcCierra()
    {
        var seed = await SeedE2EScenarioAsync();

        var provider = _fixture.BuildServiceProvider();

        var (bcPublicId, entryPublicId, _, customerId) = await RunFlowUntilCreditEntryAsync(
            seed, provider, creditAmount: 2_000m);

        // Crear otra Reserva del mismo customer (destino del credito).
        Guid targetReservaPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var targetReserva = new Reserva
            {
                NumeroReserva = "R-TARGET-V4",
                Name = "Reserva nueva del mismo cliente",
                Status = EstadoReserva.Confirmed,
                PayerId = customerId,
            };
            setupCtx.Reservas.Add(targetReserva);
            await setupCtx.SaveChangesAsync();
            targetReservaPublicId = targetReserva.PublicId;
        }

        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.AppliedToNewBooking,
                    Amount: 2_000m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: targetReservaPublicId,
                    ApprovalRequestPublicId: null,
                    Reference: null),
                userId: "user-cashier", userName: null,
                ct: CancellationToken.None);
        }

        await using var verifyCtx = _fixture.CreateDbContext();

        // BC cerrado (el saldo se consumio).
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.Closed, bc.Status);

        // NO existe ningun ManualCashMovement asociado al withdrawal.
        // (Si existiera, seria un bug: el PaymentService lo creara cuando
        // efectivamente se registre el pago en la nueva reserva.)
        var withdrawMovements = await verifyCtx.ManualCashMovements.AsNoTracking()
            .CountAsync(m => m.ClientCreditWithdrawalId != null);
        Assert.Equal(0, withdrawMovements);

        // El withdrawal SI existe en BD con el kind correcto.
        var withdrawal = await verifyCtx.ClientCreditWithdrawals.AsNoTracking()
            .FirstAsync();
        Assert.Equal(WithdrawalKind.AppliedToNewBooking, withdrawal.Kind);
        Assert.Equal(2_000m, withdrawal.Amount);
    }

    // =========================================================================
    // Variante 5: N retiros parciales hasta consumir el saldo
    // =========================================================================

    /// <summary>
    /// Variante 5: regla 12 policy (ADR-002). El cliente puede hacer N retiros
    /// parciales del mismo entry hasta consumir todo el saldo. El BC NO cierra
    /// hasta el ultimo retiro.
    ///
    /// <para>
    /// Flujo: armar entry con $1000. Hacer 3 retiros (Transfer $300 +
    /// PhysicalCash $200 + Transfer $500). El BC solo debe pasar a Closed
    /// DESPUES del ultimo retiro.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Variante5_NRetirosParciales_BcCierraSoloConElUltimoRetiro()
    {
        var seed = await SeedE2EScenarioAsync();

        var provider = _fixture.BuildServiceProvider();

        var (bcPublicId, entryPublicId, _, _) = await RunFlowUntilCreditEntryAsync(
            seed, provider, creditAmount: 1_000m);

        // Retiro 1: $300 via Transfer. BC sigue en ClientCreditApplied.
        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    WithdrawalKind.Transfer, 300m,
                    null, null, null, null),
                "user-cashier", null, CancellationToken.None);
        }

        await using (var ctx = _fixture.CreateDbContext())
        {
            var bc = await ctx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc.Status);
            Assert.Null(bc.ClosedAt);

            var entry = await ctx.ClientCreditEntries.AsNoTracking()
                .FirstAsync(e => e.PublicId == entryPublicId);
            Assert.Equal(700m, entry.RemainingBalance);
        }

        // Retiro 2: $200 via PhysicalCash. BC sigue abierto.
        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    WithdrawalKind.PhysicalCash, 200m,
                    null, null, null, null),
                "user-cashier", null, CancellationToken.None);
        }

        await using (var ctx = _fixture.CreateDbContext())
        {
            var bc = await ctx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc.Status);

            var entry = await ctx.ClientCreditEntries.AsNoTracking()
                .FirstAsync(e => e.PublicId == entryPublicId);
            Assert.Equal(500m, entry.RemainingBalance);
        }

        // Retiro 3 (ultimo): $500 via Transfer. AHORA el BC debe cerrar.
        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    WithdrawalKind.Transfer, 500m,
                    null, null, null, null),
                "user-cashier", null, CancellationToken.None);
        }

        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            // BC cerrado solo despues del 3er retiro.
            var bc = await verifyCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
            Assert.NotNull(bc.ClosedAt);

            // Reserva cancelada.
            var reserva = await verifyCtx.Reservas.AsNoTracking()
                .FirstAsync(r => r.Id == seed.ReservaId);
            Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

            // Entry totalmente consumido + 3 withdrawals.
            var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
                .Include(e => e.Withdrawals)
                .FirstAsync(e => e.PublicId == entryPublicId);
            Assert.Equal(0m, entry.RemainingBalance);
            Assert.True(entry.IsFullyConsumed);
            Assert.Equal(3, entry.Withdrawals.Count);

            // Audit BookingCancellationClosed presente UNA sola vez (solo el
            // ultimo retiro lo dispara; los 2 anteriores no deben generarlo).
            var closeAudits = await verifyCtx.AuditLogs.AsNoTracking()
                .CountAsync(a => a.Action == AuditActions.BookingCancellationClosed);
            Assert.Equal(1, closeAudits);
        }
    }

    // =========================================================================
    // Helpers privados de seed y flujos compartidos
    // =========================================================================

    private record E2ESeedResult(
        int CustomerId,
        int SupplierId,
        int ReservaId,
        int InvoiceId,
        Guid ReservaPublicId,
        Guid SupplierPublicId);

    /// <summary>
    /// Setup base para los tests E2E:
    /// <list type="bullet">
    ///   <item>Customer (TaxCondition consumidor final por default).</item>
    ///   <item>Supplier (TaxCondition RI por default).</item>
    ///   <item>Reserva en Confirmed.</item>
    ///   <item>ServicioReserva linkeando la reserva con el supplier (necesario
    ///         para que <c>DraftAsync</c> pueda inferir el SupplierId).</item>
    ///   <item>Invoice original (tipo A por default) sin CAE — el flow E2E no
    ///         exige CAE en la original; solo en la NC.</item>
    /// </list>
    /// </summary>
    private async Task<E2ESeedResult> SeedE2EScenarioAsync(int invoiceTipoComprobante = 1)
    {
        await using var ctx = _fixture.CreateDbContext();

        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // Si el caller pidio otro tipo de comprobante (Variante 1: Factura B = 6)
        // lo actualizamos antes de devolver Ids.
        if (invoiceTipoComprobante != 1)
        {
            var inv = await ctx.Invoices.FirstAsync(i => i.Id == invId);
            inv.TipoComprobante = invoiceTipoComprobante;
            await ctx.SaveChangesAsync();
        }

        // ServicioReserva es el unico campo que DraftAsync busca para inferir
        // SupplierId; sin esto el draft tira "no tiene servicios con Supplier
        // asignado" y los tests rompen con NullReferenceException.
        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = resId,
            SupplierId = supId,
            ServiceType = "Hotel",
            Description = "Hotel test E2E",
        });
        await ctx.SaveChangesAsync();

        // Leemos los PublicIds que asigno la BD.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == resId);
        var supplier = await ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supId);

        return new E2ESeedResult(
            CustomerId: custId,
            SupplierId: supId,
            ReservaId: resId,
            InvoiceId: invId,
            ReservaPublicId: reserva.PublicId,
            SupplierPublicId: supplier.PublicId);
    }

    /// <summary>
    /// Construye un <see cref="ConfirmCancellationRequest"/> con datos validos
    /// del FiscalSnapshot. Por default usa la matriz Mono-agencia x RI-supplier
    /// (igual al patron del BookingCancellationServiceTests).
    /// </summary>
    private static ConfirmCancellationRequest BuildValidConfirm(
        string agencyCondition = "Monotributo",
        string supplierCondition = "IVA_RESP_INSCRIPTO",
        string customerCondition = "Consumidor Final")
        => new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test E2E",
                AgencyTaxConditionAtEvent: agencyCondition,
                SupplierTaxConditionAtEvent: supplierCondition,
                CustomerTaxConditionAtEvent: customerCondition),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    /// <summary>
    /// Crea una NC (Invoice tipo 3/8/13 con CAE) linkeada a la factura original
    /// y la persiste. Es la simulacion del side effect que en produccion ejecuta
    /// AfipService cuando AFIP devuelve CAE para una annulacion. Devuelve el
    /// <c>Invoices.Id</c> generado.
    /// </summary>
    private async Task<int> CreateNcInvoiceAsync(
        int reservaId,
        int originalInvoiceId,
        int tipoNc,
        int numeroComprobante)
    {
        await using var ctx = _fixture.CreateDbContext();
        var nc = new Invoice
        {
            TipoComprobante = tipoNc,
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = "73000000000099",
            Resultado = "A",
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reservaId,
            OriginalInvoiceId = originalInvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();
        return nc.Id;
    }

    /// <summary>
    /// Ejecuta el flujo hasta que el ClientCreditEntry esta creado y el BC en
    /// <see cref="BookingCancellationStatus.ClientCreditApplied"/>. Usado por
    /// las variantes 3, 4 y 5 para llegar al "punto de partida" donde difieren.
    /// Devuelve los identificadores que las variantes necesitan para hacer
    /// asserts (BcPublicId, EntryPublicId, EntryId, CustomerId).
    /// </summary>
    private async Task<(Guid BcPublicId, Guid EntryPublicId, int EntryId, int CustomerId)>
        RunFlowUntilCreditEntryAsync(E2ESeedResult seed, IServiceProvider provider, decimal creditAmount)
    {
        Guid bcPublicId;
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test flujo hasta credit entry"),
                "user-vendor", "Vendor", CancellationToken.None);
            bcPublicId = draft.PublicId;

            await bcSvc.ConfirmAsync(
                draft.PublicId, BuildValidConfirm(),
                "user-vendor", "Vendor",
                requesterIsAdmin: false, ct: CancellationToken.None);
        }

        int creditNoteId = await CreateNcInvoiceAsync(
            seed.ReservaId, seed.InvoiceId, tipoNc: 3, numeroComprobante: 100);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(seed.InvoiceId, creditNoteId, CancellationToken.None);
        }

        Guid refundPublicId;
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refundDto = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, creditAmount, "ARS", DateTime.UtcNow,
                    "Transfer", null, null),
                "user-cashier", null, CancellationToken.None);
            refundPublicId = refundDto.PublicId;

            await refundSvc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, creditAmount, new List<DeductionLineRequest>()),
                "user-cashier", null, CancellationToken.None);
        }

        // Releer entry para devolver ids reales.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.BookingCancellationId == bc.Id);

        return (bcPublicId, entry.PublicId, entry.Id, seed.CustomerId);
    }
}
