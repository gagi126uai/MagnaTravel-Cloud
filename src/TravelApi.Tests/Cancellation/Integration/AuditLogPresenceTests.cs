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
/// FC1.2.7b (2026-05-18): tests "log contains action" del modulo cancelacion.
/// Cada test ejerce UNA operacion del flujo via los services reales contra
/// <see cref="PostgresIntegrationFixture"/> y verifica que la fila
/// <c>AuditLogs</c> correspondiente quedo persistida con el <c>Action</c>
/// esperado (constantes de <see cref="AuditActions"/>).
///
/// <para>
/// <b>Por que estos tests son necesarios</b>: el flujo de cancelacion/refund
/// tiene impacto fiscal (Ley 25.345, NC, retenciones). Si una accion no deja
/// audit log, el contador no puede reconstruir el evento ni explicarlo en una
/// inspeccion AFIP. Validar que cada constante de <see cref="AuditActions"/>
/// efectivamente se emite previene drift entre "lo que el codigo cree que
/// audita" y "lo que la BD termina persistiendo".
/// </para>
///
/// <para>
/// <b>Diferencia con <see cref="CancellationFlowE2ETests"/></b>: el E2E
/// recorre el flujo completo y valida estado de negocio + 7 actions clave.
/// Estos tests son granulares: 1 operacion → 1 audit action, mas focales para
/// detectar si un refactor accidentalmente saca el <c>LogBusinessEventAsync</c>
/// de una rama puntual (ej. el Force ARCA o el reversal).
/// </para>
///
/// <para>
/// <b>Estrategia para AuditService real</b>: usamos
/// <see cref="PostgresIntegrationFixture.BuildServiceProvider"/> que registra
/// <c>AuditService</c> real (persiste en Postgres). Si lo mockearamos, el test
/// pasaria sin haber emitido nada — exactamente el bug que queremos detectar.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditLogPresenceTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public AuditLogPresenceTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // 1. Draft → BookingCancellationDrafted
    // =========================================================================

    /// <summary>
    /// Verifica que <c>DraftAsync</c> deja un audit log con
    /// <see cref="AuditActions.BookingCancellationDrafted"/>. Es el punto de
    /// entrada del flujo: si esto no audita, el contador pierde traza de
    /// "quien arranco la cancelacion".
    /// </summary>
    [Fact]
    public async Task Draft_LogeaBookingCancellationDrafted()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit draft"),
                userId: "user-vendor", userName: "Vendedor",
                ct: CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.BookingCancellationDrafted,
            entityName: AuditActions.BookingCancellationEntityName,
            userId: "user-vendor");
    }

    // =========================================================================
    // 2. Confirm → BookingCancellationConfirmed
    // =========================================================================

    /// <summary>
    /// Verifica que <c>ConfirmAsync</c> deja audit con
    /// <see cref="AuditActions.BookingCancellationConfirmed"/>. Es la transicion
    /// fiscal mas critica: dispara la NC en AFIP. Un audit faltante aca rompe
    /// la trazabilidad fiscal end-to-end.
    /// </summary>
    [Fact]
    public async Task Confirm_LogeaBookingCancellationConfirmed()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit confirm"),
                "user-vendor", "Vendedor", CancellationToken.None);

            await bcSvc.ConfirmAsync(
                draft.PublicId,
                BuildValidConfirm(),
                "user-vendor", "Vendedor",
                requesterIsAdmin: false,
                ct: CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.BookingCancellationConfirmed,
            entityName: AuditActions.BookingCancellationEntityName,
            userId: "user-vendor");
    }

    // =========================================================================
    // 3. Abort → BookingCancellationAborted
    // =========================================================================

    /// <summary>
    /// Verifica que <c>AbortAsync</c> deja audit con
    /// <see cref="AuditActions.BookingCancellationAborted"/>. Aunque Abort no
    /// tiene impacto fiscal (solo cierra el draft), el audit es necesario para
    /// que el dashboard muestre "el vendedor abortio el draft a las HH:MM por
    /// motivo X". Sin esto, no hay forma de explicar por que un BC quedo en
    /// <c>Aborted</c>.
    /// </summary>
    [Fact]
    public async Task Abort_LogeaBookingCancellationAborted()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit abort"),
                "user-vendor", null, CancellationToken.None);

            await bcSvc.AbortAsync(
                draft.PublicId,
                "Cliente se arrepintio antes de confirmar",
                "user-vendor",
                CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.BookingCancellationAborted,
            entityName: AuditActions.BookingCancellationEntityName,
            userId: "user-vendor");
    }

    // =========================================================================
    // 4. ForceArcaConfirmation → BookingCancellationArcaConfirmedManually
    // =========================================================================

    /// <summary>
    /// Verifica que <c>ForceArcaConfirmationAsync</c> deja audit con
    /// <see cref="AuditActions.BookingCancellationArcaConfirmedManually"/>.
    /// Este es el audit MAS CRITICO del modulo: cualquier transicion fiscal
    /// manual saltea el flujo automatico AFIP, y la auditoria fiscal lo busca
    /// por nombre exacto para distinguirlo de
    /// <see cref="AuditActions.BookingCancellationArcaSucceeded"/> (callback).
    /// Si la action no se emite, parece que la NC se aprobo via callback —
    /// peligro fiscal serio.
    /// </summary>
    [Fact]
    public async Task ForceArcaConfirmation_LogeaBookingCancellationArcaConfirmedManually()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        // Setup: Draft + Confirm → BC en AwaitingFiscalConfirmation (precondicion
        // del ForceArca).
        Guid bcPublicId;
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit force"),
                "user-admin", "Admin", CancellationToken.None);
            bcPublicId = draft.PublicId;

            await bcSvc.ConfirmAsync(
                draft.PublicId, BuildValidConfirm(),
                "user-admin", "Admin",
                requesterIsAdmin: false, ct: CancellationToken.None);
        }

        // Crear NC valida + approval aprobado (precondicion del ForceArca).
        Guid creditNotePublicId;
        Guid approvalPublicId;
        await using (var setupCtx = _fixture.CreateDbContext())
        {
            var creditNote = new Invoice
            {
                TipoComprobante = 3, // NC A
                PuntoDeVenta = 1,
                NumeroComprobante = 99,
                CAE = "73000000000099",
                Resultado = "A",
                ImporteTotal = 1000m,
                ImporteNeto = 826.45m,
                ImporteIva = 173.55m,
                ReservaId = seed.ReservaId,
                OriginalInvoiceId = seed.InvoiceId,
                CreatedAt = DateTime.UtcNow,
            };
            setupCtx.Invoices.Add(creditNote);
            await setupCtx.SaveChangesAsync();
            creditNotePublicId = creditNote.PublicId;

            var bcId = (await setupCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId)).Id;
            var approval = new ApprovalRequest
            {
                RequestType = ApprovalRequestType.InvariantOverride,
                EntityType = "BookingCancellation",
                EntityId = bcId,
                RequestedByUserId = "user-admin",
                RequestedAt = DateTime.UtcNow,
                Status = ApprovalStatus.Approved,
                ResolvedByUserId = "admin-test",
                ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Reason = "Test override aprobado para force arca",
            };
            setupCtx.ApprovalRequests.Add(approval);
            await setupCtx.SaveChangesAsync();
            approvalPublicId = approval.PublicId;
        }

        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            await bcSvc.ForceArcaConfirmationAsync(
                bcPublicId,
                new ForceArcaConfirmationRequest(
                    CreditNoteInvoicePublicId: creditNotePublicId,
                    ApprovalRequestPublicId: approvalPublicId,
                    Reason: "Test audit force arca confirmation manual"),
                userId: "user-admin", userName: "Admin",
                ct: CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.BookingCancellationArcaConfirmedManually,
            entityName: AuditActions.BookingCancellationEntityName,
            userId: "user-admin");
    }

    // =========================================================================
    // 5. OnArcaSucceeded (bridge) → BookingCancellationArcaSucceeded
    // =========================================================================

    /// <summary>
    /// Verifica que el callback <c>OnArcaSucceededAsync</c> (invocado por el
    /// job Hangfire cuando AFIP devuelve CAE) deja audit con
    /// <see cref="AuditActions.BookingCancellationArcaSucceeded"/>. Discrimina
    /// del audit manual <c>BookingCancellationArcaConfirmedManually</c> — el
    /// reporte fiscal usa AMBOS para responder "que % paso por callback vs
    /// manual" (idealmente 99% callback).
    /// </summary>
    [Fact]
    public async Task OnArcaSucceeded_LogeaBookingCancellationArcaSucceeded()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        // Setup: Draft + Confirm (BC queda en AwaitingFiscalConfirmation).
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit arca succeeded"),
                "user-vendor", "Vendedor", CancellationToken.None);
            await bcSvc.ConfirmAsync(
                draft.PublicId, BuildValidConfirm(),
                "user-vendor", "Vendedor",
                requesterIsAdmin: false, ct: CancellationToken.None);
        }

        // Crear NC simulando que AFIP la devolvio con CAE.
        var creditNoteId = await CreateNcInvoiceAsync(seed.ReservaId, seed.InvoiceId);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(
                originatingInvoiceId: seed.InvoiceId,
                creditNoteInvoiceId: creditNoteId,
                ct: CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.BookingCancellationArcaSucceeded,
            entityName: AuditActions.BookingCancellationEntityName);
    }

    // =========================================================================
    // 6. Allocate → OperatorRefundAllocated
    // =========================================================================

    /// <summary>
    /// Verifica que <c>AllocateAsync</c> deja audit con
    /// <see cref="AuditActions.OperatorRefundAllocated"/>. Cada allocation es
    /// un evento contable: impacta el cap del refund + crea credito al cliente.
    /// Sin audit no se puede reconstruir el reparto N:M ante una inspeccion.
    /// </summary>
    [Fact]
    public async Task Allocate_LogeaOperatorRefundAllocated()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        var bcPublicId = await RunUntilAwaitingRefundAsync(seed, provider);

        Guid refundPublicId;
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refund = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, 2_000m, "ARS", DateTime.UtcNow,
                    "Transfer", null, null),
                "user-cashier", null, CancellationToken.None);
            refundPublicId = refund.PublicId;

            await refundSvc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, 2_000m, new List<DeductionLineRequest>()),
                "user-cashier", null, CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.OperatorRefundAllocated,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            userId: "user-cashier");
    }

    // =========================================================================
    // 7. VoidAllocation → OperatorRefundAllocationVoided
    // =========================================================================

    /// <summary>
    /// Verifica que <c>VoidAllocationAsync</c> deja audit con
    /// <see cref="AuditActions.OperatorRefundAllocationVoided"/>. El void
    /// libera cap del refund y resetea credito del cliente — sin audit, una
    /// allocation desaparecida quedaria inexplicada en reportes.
    /// </summary>
    [Fact]
    public async Task VoidAllocation_LogeaOperatorRefundAllocationVoided()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        var bcPublicId = await RunUntilAwaitingRefundAsync(seed, provider);

        // Crear refund + allocation activa para luego voidear.
        Guid allocationPublicId;
        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refund = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, 1_500m, "ARS", DateTime.UtcNow,
                    "Transfer", null, null),
                "user-cashier", null, CancellationToken.None);

            var allocation = await refundSvc.AllocateAsync(
                refund.PublicId,
                new AllocateRefundRequest(bcPublicId, 1_500m, new List<DeductionLineRequest>()),
                "user-cashier", null, CancellationToken.None);
            allocationPublicId = allocation.PublicId;
        }

        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await refundSvc.VoidAllocationAsync(
                allocationPublicId,
                new VoidAllocationRequest("Test audit void: cashier se equivoco al imputar"),
                "user-cashier", null, CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.OperatorRefundAllocationVoided,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            userId: "user-cashier");
    }

    // =========================================================================
    // 8. RecordReceived → OperatorRefundReceivedRegistered
    // =========================================================================

    /// <summary>
    /// Verifica que <c>RecordReceivedAsync</c> deja audit con
    /// <see cref="AuditActions.OperatorRefundReceivedRegistered"/>. Es el unico
    /// rastro de "el operador devolvio X plata el dia Y" para contabilidad —
    /// faltarlo seria perder el ingreso fisico en la auditoria.
    /// </summary>
    [Fact]
    public async Task RecordReceived_LogeaOperatorRefundReceivedRegistered()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, 3_500m, "ARS", DateTime.UtcNow,
                    "Transfer", "OP-AUDIT-TEST", "Test audit record received"),
                userId: "user-cashier", userName: "Cajero", ct: CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.OperatorRefundReceivedRegistered,
            entityName: AuditActions.OperatorRefundReceivedEntityName,
            userId: "user-cashier");
    }

    // =========================================================================
    // 9. Withdraw (Transfer) → ClientCreditWithdrawn
    // =========================================================================

    /// <summary>
    /// Verifica que <c>WithdrawAsync</c> con <see cref="WithdrawalKind.Transfer"/>
    /// deja audit con <see cref="AuditActions.ClientCreditWithdrawn"/>. Es el
    /// audit base que cubre todos los kinds de retiro (PhysicalCash, Transfer,
    /// KeptAsCredit, AppliedToNewBooking, ReversedToOperator). Sin esto, la
    /// caja no puede explicar de donde salio cada egreso.
    /// </summary>
    [Fact]
    public async Task Withdraw_LogeaClientCreditWithdrawn()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        var (entryPublicId, _) = await RunUntilCreditEntryAsync(seed, provider, creditAmount: 1_000m);

        using (var scope = provider.CreateScope())
        {
            var creditSvc = scope.ServiceProvider.GetRequiredService<IClientCreditService>();
            await creditSvc.WithdrawAsync(
                entryPublicId,
                new WithdrawClientCreditRequest(
                    Kind: WithdrawalKind.Transfer,
                    Amount: 1_000m,
                    PaymentMethodOverride: "Transfer-BBVA",
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: null,
                    Reference: "TX-AUDIT-001"),
                "user-cashier", "Cajero", CancellationToken.None);
        }

        await AssertAuditExistsAsync(
            action: AuditActions.ClientCreditWithdrawn,
            entityName: AuditActions.ClientCreditWithdrawalEntityName,
            userId: "user-cashier");
    }

    // =========================================================================
    // 10. Withdraw (ReversedToOperator) → ClientRefundReversalApproved
    // =========================================================================

    /// <summary>
    /// Verifica que <c>WithdrawAsync</c> con
    /// <see cref="WithdrawalKind.ReversedToOperator"/> deja AMBOS audits:
    /// <list type="bullet">
    ///   <item><see cref="AuditActions.ClientCreditWithdrawn"/> (audit base).</item>
    ///   <item><see cref="AuditActions.ClientRefundReversalApproved"/> (audit
    ///         reforzado, ADR-002 §8 — el daily egress report lo destaca como
    ///         "reversal post-egreso", evento atipico que el contador revisa
    ///         manualmente).</item>
    /// </list>
    /// Este test es el unico que valida los DOS audits del kind reversal —
    /// si refactorizan el flujo y olvidan el reforzado, este test rompe.
    /// </summary>
    [Fact]
    public async Task Withdraw_ReversedToOperator_LogeaClientRefundReversalApproved()
    {
        var seed = await SeedScenarioAsync();
        var provider = _fixture.BuildServiceProvider();

        var (entryPublicId, entryId) = await RunUntilCreditEntryAsync(seed, provider, creditAmount: 800m);

        // Crear approval ClientRefundReversal aprobado scoped al entry.
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
                ResolvedByUserId = "admin-test",
                ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Reason = "Cliente devuelve la plata para reasignar al operador",
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
                    Amount: 800m,
                    PaymentMethodOverride: null,
                    AppliedToReservaPublicId: null,
                    ApprovalRequestPublicId: approvalPublicId,
                    Reference: null),
                "user-cashier", "Cajero", CancellationToken.None);
        }

        // El test pone el foco en el AUDIT REFORZADO (la regla 10 del brief).
        // El audit base ClientCreditWithdrawn ya esta cubierto por el test #9.
        await AssertAuditExistsAsync(
            action: AuditActions.ClientRefundReversalApproved,
            entityName: AuditActions.ClientCreditEntryEntityName,
            userId: "user-cashier");
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    /// <summary>
    /// Verifica que existe AL MENOS UNA fila en <c>AuditLogs</c> con la
    /// <paramref name="action"/> esperada. Opcionalmente valida
    /// <paramref name="entityName"/> y <paramref name="userId"/> si se pasan
    /// (los tests con bridges <see cref="IInvoiceAnnulmentBcBridge"/> NO pasan
    /// userId porque el callback usa el userId del Confirm original).
    /// </summary>
    private async Task AssertAuditExistsAsync(string action, string? entityName = null, string? userId = null)
    {
        await using var verifyCtx = _fixture.CreateDbContext();
        var audit = await verifyCtx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == action)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.False(string.IsNullOrWhiteSpace(audit!.EntityId),
            $"El audit '{action}' debe tener EntityId no vacio.");

        if (entityName != null)
        {
            Assert.Equal(entityName, audit.EntityName);
        }

        if (userId != null)
        {
            Assert.Equal(userId, audit.UserId);
        }
    }

    private record AuditSeed(
        int CustomerId,
        int SupplierId,
        int ReservaId,
        int InvoiceId,
        Guid ReservaPublicId,
        Guid SupplierPublicId);

    /// <summary>
    /// Setup base reutilizado por todos los tests: Customer + Supplier +
    /// Reserva + Invoice + ServicioReserva. Sigue el mismo patron que
    /// <c>BookingCancellationServiceTests.SeedScenarioAsync</c> +
    /// <c>CancellationFlowE2ETests.SeedE2EScenarioAsync</c>.
    /// </summary>
    private async Task<AuditSeed> SeedScenarioAsync()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // ServicioReserva: sin esto DraftAsync no puede inferir SupplierId. Le damos NetCost/Currency
        // en ARS porque el RefundCap de la linea se topea por el NetCost del servicio (con NetCost 0 el
        // cap seria 0 aunque le paguemos al operador).
        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = resId,
            SupplierId = supId,
            ServiceType = "Hotel",
            Description = "Hotel test audit",
            Currency = "ARS",
            SalePrice = 60_000m,
            NetCost = 50_000m,
        });
        await ctx.SaveChangesAsync();

        // (2026-07-03) Le pagamos al operador para que la anulacion tenga circuito real de reembolso
        // (RefundCap > 0). Sin esto la BC se auto-cerraria en la transicion post-CAE ("sin plata al
        // operador cierra sola") y los tests de Allocate/Void/Withdraw no llegarian a
        // AwaitingOperatorRefund: el allocate rebotaria con INV-093.
        await CancellationTestData.SeedSupplierPaymentAsync(ctx, supId, resId, 50_000m);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == resId);
        var supplier = await ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supId);

        return new AuditSeed(custId, supId, resId, invId, reserva.PublicId, supplier.PublicId);
    }

    /// <summary>
    /// <c>ConfirmCancellationRequest</c> con valores fiscales por default
    /// validos para la matriz Monotributo-agencia + RI-supplier (la mas comun
    /// en el escenario actual de MagnaTravel-Cloud Mono).
    /// </summary>
    private static ConfirmCancellationRequest BuildValidConfirm()
        => new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test audit log presence",
                AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "Consumidor Final"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    /// <summary>
    /// Crea una NC (Invoice tipo 3 con CAE) linkeada a la factura original.
    /// Simula el side effect que en produccion ejecuta AfipService al recibir
    /// CAE. Devuelve <c>Invoices.Id</c>.
    /// </summary>
    private async Task<int> CreateNcInvoiceAsync(int reservaId, int originalInvoiceId)
    {
        await using var ctx = _fixture.CreateDbContext();
        var nc = new Invoice
        {
            TipoComprobante = 3, // NC A
            PuntoDeVenta = 1,
            NumeroComprobante = 2,
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
    /// Avanza el flujo hasta <see cref="BookingCancellationStatus.AwaitingOperatorRefund"/>:
    /// Draft → Confirm → simular AFIP OK via bridge. Reusado por los tests que
    /// necesitan ejercer Allocate / VoidAllocation.
    /// Devuelve el <c>PublicId</c> del BC ya listo para recibir refund.
    /// </summary>
    private async Task<Guid> RunUntilAwaitingRefundAsync(AuditSeed seed, IServiceProvider provider)
    {
        Guid bcPublicId;
        using (var scope = provider.CreateScope())
        {
            var bcSvc = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var draft = await bcSvc.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Test audit pre-allocate"),
                "user-vendor", "Vendor", CancellationToken.None);
            bcPublicId = draft.PublicId;

            await bcSvc.ConfirmAsync(
                draft.PublicId, BuildValidConfirm(),
                "user-vendor", "Vendor",
                requesterIsAdmin: false, ct: CancellationToken.None);
        }

        int creditNoteId = await CreateNcInvoiceAsync(seed.ReservaId, seed.InvoiceId);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(seed.InvoiceId, creditNoteId, CancellationToken.None);
        }

        return bcPublicId;
    }

    /// <summary>
    /// Avanza el flujo hasta tener un <see cref="ClientCreditEntry"/> con saldo
    /// disponible: Draft → Confirm → bridge AFIP OK → RecordReceived → Allocate.
    /// Reusado por los tests de Withdraw (#9 y #10).
    /// Devuelve el <c>PublicId</c> y el <c>Id</c> del entry (el Id es necesario
    /// para crear ApprovalRequests scoped al entry).
    /// </summary>
    private async Task<(Guid EntryPublicId, int EntryId)> RunUntilCreditEntryAsync(
        AuditSeed seed,
        IServiceProvider provider,
        decimal creditAmount)
    {
        var bcPublicId = await RunUntilAwaitingRefundAsync(seed, provider);

        using (var scope = provider.CreateScope())
        {
            var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refund = await refundSvc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(
                    seed.SupplierPublicId, creditAmount, "ARS", DateTime.UtcNow,
                    "Transfer", null, null),
                "user-cashier", null, CancellationToken.None);

            await refundSvc.AllocateAsync(
                refund.PublicId,
                new AllocateRefundRequest(bcPublicId, creditAmount, new List<DeductionLineRequest>()),
                "user-cashier", null, CancellationToken.None);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var entry = await verifyCtx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(e => e.BookingCancellation.PublicId == bcPublicId);
        return (entry.PublicId, entry.Id);
    }
}
