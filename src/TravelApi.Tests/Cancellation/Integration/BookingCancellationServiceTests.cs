using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.1 v3 §8 (2026-05-17): tests integracion del
/// <see cref="BookingCancellationService"/>. Usa Postgres real via
/// <see cref="PostgresIntegrationFixture"/> para validar:
///  - CHECK SQL chk_BookingCancellations_fiscalsnapshot_consistent (INV-118).
///  - El owned type FiscalSnapshot se persiste y se lee correctamente.
///  - La cross-reference fiscal Invoice ↔ BC ↔ ApprovalRequest funciona.
///
/// <para>
/// <b>Por que NO unit tests con InMemory</b>: InMemory ignora CHECK constraints
/// SQL y el xmin concurrency token. Esos son justamente los invariantes que el
/// service apoya. Un test que pasa con InMemory pero falla en prod es peor que
/// no tener test.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BookingCancellationServiceTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public BookingCancellationServiceTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Helpers de armado del service y data.
    // =========================================================================

    /// <summary>
    /// Arma el service con dependencias mockeadas pero contra Postgres real.
    /// El IInvoiceService queda mockeado (no llamamos AFIP). El IApprovalRequestService
    /// es real para que MarkConsumedAsync efectivamente persista.
    /// </summary>
    private (BookingCancellationService service,
             AppDbContext ctx,
             Mock<IInvoiceService> invoiceMock,
             Mock<IOperationalFinanceSettingsService> settingsMock)
        BuildService(AppDbContext? ctxIn = null, bool featureFlagOn = true)
    {
        var ctx = ctxIn ?? _fixture.CreateDbContext();

        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = featureFlagOn,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var approvalService = new ApprovalRequestService(ctx, approvalSettings.Object);

        var auditMock = new Mock<IAuditService>();

        // FC1.3.3: el ctor ahora pide tambien el calculator y el contador de admins.
        // Para tests FC1.2 alcanza con mocks no configurados — el flag
        // EnablePartialCreditNotes esta OFF por default, asi que el calculator nunca
        // se invoca y el contador tampoco.
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalService,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return (service, ctx, invoiceMock, settingsMock);
    }

    /// <summary>
    /// Crea un escenario base: Customer + Supplier + Reserva + Invoice + 1 servicio
    /// que vincula la reserva con el supplier (necesario para que <c>DraftAsync</c>
    /// pueda inferir SupplierId).
    /// </summary>
    private async Task<(int CustomerId, int SupplierId, int ReservaId, int InvoiceId, Guid ReservaPublicId)>
        SeedScenarioAsync(AppDbContext ctx)
    {
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = resId,
            SupplierId = supId,
            ServiceType = "Hotel",
            Description = "Hotel test",
        });
        await ctx.SaveChangesAsync();

        var reservaPublicId = (await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == resId)).PublicId;
        return (custId, supId, resId, invId, reservaPublicId);
    }

    /// <summary>Crea un ApprovalRequest InvariantOverride aprobado para un BC.</summary>
    private static async Task<ApprovalRequest> SeedApprovedOverrideAsync(
        AppDbContext ctx,
        int bcId,
        string userId)
    {
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.InvariantOverride,
            EntityType = "BookingCancellation",
            EntityId = bcId,
            RequestedByUserId = userId,
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Approved,
            ResolvedByUserId = "admin-test",
            ResolvedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Test override aprobado para BC " + bcId,
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();
        return approval;
    }

    private static ConfirmCancellationRequest BuildValidConfirm(bool isOverride = false, Guid? approvalPublicId = null)
        => new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test justification",
                AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "Consumidor Final"),
            IsAdminOverride: isOverride,
            OverrideReason: isOverride ? "Motivo override min 20 chars para test" : null,
            ApprovalRequestPublicId: approvalPublicId);

    // =========================================================================
    // DraftAsync
    // =========================================================================

    [Fact]
    public async Task DraftAsync_ConInvoiceActiva_OK_CreaBcEnDrafted()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Cliente cambio de plan"),
            "user-vendor",
            "Vendedor Test",
            CancellationToken.None);

        Assert.Equal("Drafted", dto.Status);
        Assert.Equal(seed.ReservaPublicId, dto.ReservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
        Assert.Equal(seed.ReservaId, bc.ReservaId);
        Assert.Equal(seed.InvoiceId, bc.OriginatingInvoiceId);
        Assert.Equal("user-vendor", bc.DraftedByUserId);
    }

    [Fact]
    public async Task DraftAsync_FeatureFlagOff_Rechaza()
    {
        var (service, ctx, _, _) = BuildService(featureFlagOn: false);
        var seed = await SeedScenarioAsync(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Cliente cambio de plan"),
                "user-vendor", "Vendor", CancellationToken.None));
        Assert.Contains("no esta habilitado", ex.Message);
    }

    [Fact]
    public async Task DraftAsync_DraftPuroExistente_ReutilizaElMismoBc_NoCreaOtro()
    {
        // B1 (commit 464339c / f644eea): un segundo DraftAsync sobre una reserva que ya
        // tiene un draft PURO (Status Drafted, sin comprobantes vivos) ya NO rechaza con
        // INV-081 — REUTILIZA el draft existente (TryResolveExistingBcAsync). INV-081 queda
        // reservado para el caso real peligroso (BC no liberable / con NC viva), cubierto en
        // BookingCancellationDraftRetryPolicyTests. Aca verificamos el reuse: mismo PublicId
        // y una sola fila en la tabla.
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var first = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Primer intento"),
            "user-vendor", null, CancellationToken.None);

        var second = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Segundo intento"),
            "user-vendor", null, CancellationToken.None);

        Assert.Equal(first.PublicId, second.PublicId);

        var count = await ctx.BookingCancellations
            .CountAsync(bc => bc.ReservaId == seed.ReservaId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DraftAsync_InvoiceOriginalYaAnulada_Rechaza_NoInvoiceActiva()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var invoice = await ctx.Invoices.FirstAsync(i => i.Id == seed.InvoiceId);
        invoice.AnnulmentStatus = AnnulmentStatus.Succeeded;
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DraftAsync(
                new DraftCancellationRequest(seed.ReservaPublicId, "Cliente cancela"),
                "user-vendor", null, CancellationToken.None));
        Assert.Contains("no tiene factura activa", ex.Message);
    }

    // =========================================================================
    // ConfirmAsync
    // =========================================================================

    [Fact]
    public async Task ConfirmAsync_GuardsOK_TransicionaAwaitingFiscalConfirmation()
    {
        var (service, ctx, invoiceMock, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Cliente arrepentido"),
            "user-vendor", "Vendor", CancellationToken.None);

        var confirmed = await service.ConfirmAsync(
            draft.PublicId,
            BuildValidConfirm(),
            "user-vendor", "Vendor",
            requesterIsAdmin: false,
            CancellationToken.None);

        Assert.Equal("AwaitingFiscalConfirmation", confirmed.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Reserva)
            .FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);
        Assert.NotNull(bc.ConfirmedWithClientAt);
        Assert.Equal("user-vendor", bc.ConfirmedByUserId);
        Assert.NotNull(bc.OperatorRefundDueBy);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, bc.Reserva.Status);

        // Despues del fix F5, el bypass del approval del InvoiceAnnulment requiere
        // que el override del BC haya sido aprobado (approvalRequest != null).
        // Como este escenario NO tiene override (BuildValidConfirm() sin params +
        // requesterIsAdmin: false), approvalRequest queda en null y el service
        // propaga requesterIsAdmin: false al EnqueueAnnulmentAsync. La NC entonces
        // sigue el approval workflow fiscal normal (OPS-FISCAL-001 plan v3 §13).
        invoiceMock.Verify(
            s => s.EnqueueAnnulmentAsync(
                seed.InvoiceId,
                "user-vendor",
                "Vendor",
                It.IsAny<string>(),
                false,
                It.IsAny<CancellationToken>(),
                null),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAsync_SnapshotInconsistente_Rechaza_INV118()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test snapshot inconsistente"),
            "user-vendor", null, CancellationToken.None);

        var badRequest = new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test",
                AgencyTaxConditionAtEvent: "Garbage",
                SupplierTaxConditionAtEvent: "Garbage",
                CustomerTaxConditionAtEvent: "Garbage"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(draft.PublicId, badRequest, "user-vendor", null, false, CancellationToken.None));
        Assert.Equal("INV-118", ex.InvariantCode);
    }

    [Fact]
    public async Task ConfirmAsync_ConIsAdminOverrideSinApproval_Rechaza()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test override sin approval"),
            "user-vendor", null, CancellationToken.None);

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            service.ConfirmAsync(
                draft.PublicId,
                BuildValidConfirm(isOverride: true, approvalPublicId: Guid.NewGuid()),
                "user-vendor", null, true, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmAsync_ConIsAdminOverrideConApprovalAprobado_OK_MarkConsumed()
    {
        var (service, ctx, invoiceMock, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test override OK"),
            "user-admin", "Admin", CancellationToken.None);

        var bcEntity = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        var approval = await SeedApprovedOverrideAsync(ctx, bcEntity.Id, "user-admin");

        var confirmed = await service.ConfirmAsync(
            draft.PublicId,
            BuildValidConfirm(isOverride: true, approvalPublicId: approval.PublicId),
            "user-admin", "Admin", true, CancellationToken.None);

        Assert.Equal("AwaitingFiscalConfirmation", confirmed.Status);

        var refreshedApproval = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, refreshedApproval.Status);

        invoiceMock.Verify(
            s => s.EnqueueAnnulmentAsync(
                seed.InvoiceId,
                "user-admin", "Admin",
                It.Is<string>(r => r.Contains("BC override")),
                true,
                It.IsAny<CancellationToken>(),
                approval.Id),
            Times.Once);
    }

    // =========================================================================
    // AbortAsync
    // =========================================================================

    [Fact]
    public async Task AbortAsync_EnDrafted_OK_TransicionaAborted()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test abort"),
            "user-vendor", null, CancellationToken.None);

        var aborted = await service.AbortAsync(
            draft.PublicId, "Vendedor se equivoco", "user-vendor", CancellationToken.None);

        Assert.Equal("Aborted", aborted.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.Aborted, bc.Status);
        Assert.NotNull(bc.ClosedAt);
    }

    [Fact]
    public async Task AbortAsync_Idempotente_SegundaLlamadaNoOp()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test abort idempotente"),
            "user-vendor", null, CancellationToken.None);

        await service.AbortAsync(draft.PublicId, "Primer abort", "user-vendor", CancellationToken.None);
        var second = await service.AbortAsync(draft.PublicId, "Segundo abort", "user-vendor", CancellationToken.None);

        Assert.Equal("Aborted", second.Status);
    }

    [Fact]
    public async Task AbortAsync_NoEnDrafted_Rechaza()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test abort en Awaiting"),
            "user-vendor", null, CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-vendor", null, false, CancellationToken.None);

        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.AbortAsync(draft.PublicId, "Demasiado tarde", "user-vendor", CancellationToken.None));
    }

    // =========================================================================
    // ForceArcaConfirmationAsync (BR-V2-01)
    // =========================================================================

    [Fact]
    public async Task ForceArca_ConApprovalAprobadoYNCValida_OK_TransicionaAwaitingOperatorRefund()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test ForceArca"),
            "user-admin", "Admin", CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-admin", "Admin", false, CancellationToken.None);

        var creditNote = new Invoice
        {
            TipoComprobante = 3,
            PuntoDeVenta = 1,
            NumeroComprobante = 2,
            CAE = "73000000000000",
            Resultado = "A",
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = seed.ReservaId,
            OriginalInvoiceId = seed.InvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bcEntity = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        var approval = await SeedApprovedOverrideAsync(ctx, bcEntity.Id, "user-admin");

        var result = await service.ForceArcaConfirmationAsync(
            draft.PublicId,
            new ForceArcaConfirmationRequest(
                CreditNoteInvoicePublicId: creditNote.PublicId,
                ApprovalRequestPublicId: approval.PublicId,
                Reason: "Callback Hangfire fallo, restauro estado manual"),
            "user-admin", "Admin", CancellationToken.None);

        Assert.Equal("AwaitingOperatorRefund", result.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Reserva)
            .FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        Assert.Equal(creditNote.Id, bc.CreditNoteInvoiceId);
        Assert.NotNull(bc.ArcaConfirmedManuallyAt);
        Assert.Equal("user-admin", bc.ArcaConfirmedManuallyByUserId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, bc.Reserva.Status);

        var refreshedApproval = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Consumed, refreshedApproval.Status);
    }

    [Fact]
    public async Task ForceArca_SinApproval_RechazaApprovalRequired()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test ForceArca sin approval"),
            "user-admin", null, CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-admin", null, false, CancellationToken.None);

        var creditNote = new Invoice
        {
            TipoComprobante = 3,
            PuntoDeVenta = 1,
            NumeroComprobante = 2,
            CAE = "73000000000000",
            Resultado = "A",
            ImporteTotal = 1000m,
            ReservaId = seed.ReservaId,
            OriginalInvoiceId = seed.InvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            service.ForceArcaConfirmationAsync(
                draft.PublicId,
                new ForceArcaConfirmationRequest(
                    CreditNoteInvoicePublicId: creditNote.PublicId,
                    ApprovalRequestPublicId: Guid.NewGuid(),
                    Reason: "Test sin approval valido para forzar"),
                "user-admin", null, CancellationToken.None));
    }

    [Fact]
    public async Task ForceArca_BcYaTransicionado_NoOpRetornaActual()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test ForceArca no-op"),
            "user-admin", null, CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-admin", null, false, CancellationToken.None);

        var bcEntity = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == draft.PublicId);
        bcEntity.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        await ctx.SaveChangesAsync();

        var approval = await SeedApprovedOverrideAsync(ctx, bcEntity.Id, "user-admin");

        var creditNote = new Invoice
        {
            TipoComprobante = 3,
            PuntoDeVenta = 1,
            NumeroComprobante = 99,
            CAE = "73000000000001",
            Resultado = "A",
            ImporteTotal = 1000m,
            ReservaId = seed.ReservaId,
            OriginalInvoiceId = seed.InvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var result = await service.ForceArcaConfirmationAsync(
            draft.PublicId,
            new ForceArcaConfirmationRequest(
                CreditNoteInvoicePublicId: creditNote.PublicId,
                ApprovalRequestPublicId: approval.PublicId,
                Reason: "Test no-op porque BC ya transiciono"),
            "user-admin", null, CancellationToken.None);

        Assert.Equal("AwaitingOperatorRefund", result.Status);

        var refreshedApproval = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Approved, refreshedApproval.Status);
    }

    // =========================================================================
    // Bridge callbacks (IInvoiceAnnulmentBcBridge)
    // =========================================================================

    [Fact]
    public async Task OnArcaSucceededAsync_ConBcAwaitingFiscalConfirmation_TransicionaOK()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test callback OK"),
            "user-vendor", "Vendor", CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-vendor", "Vendor", false, CancellationToken.None);

        var creditNote = new Invoice
        {
            TipoComprobante = 3,
            PuntoDeVenta = 1,
            NumeroComprobante = 2,
            CAE = "73000000000099",
            Resultado = "A",
            ImporteTotal = 1000m,
            ReservaId = seed.ReservaId,
            OriginalInvoiceId = seed.InvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        await ((IInvoiceAnnulmentBcBridge)service).OnArcaSucceededAsync(
            seed.InvoiceId, creditNote.Id, CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Reserva)
            .FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        Assert.Equal(creditNote.Id, bc.CreditNoteInvoiceId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, bc.Reserva.Status);
        Assert.Null(bc.ArcaConfirmedManuallyAt);
    }

    [Fact]
    public async Task OnArcaSucceededAsync_SinBcMatchante_NoOp()
    {
        var (service, _, _, _) = BuildService();

        // Llamamos al bridge sin que exista ningun BC en AwaitingFiscalConfirmation.
        // El metodo debe loguear warning y retornar sin tirar.
        await ((IInvoiceAnnulmentBcBridge)service).OnArcaSucceededAsync(
            originatingInvoiceId: 9999, creditNoteInvoiceId: 8888, CancellationToken.None);

        // Si llegamos aca sin exception, OK.
    }

    [Fact]
    public async Task OnArcaFailedAsync_ConBcAwaitingFiscalConfirmation_TransicionaArcaRejected()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test callback rejection"),
            "user-vendor", "Vendor", CancellationToken.None);
        await service.ConfirmAsync(draft.PublicId, BuildValidConfirm(), "user-vendor", "Vendor", false, CancellationToken.None);

        var afipError = "AFIP devolvio CAE rechazado: Concepto invalido para tipo de cliente Consumidor Final.";
        await ((IInvoiceAnnulmentBcBridge)service).OnArcaFailedAsync(
            seed.InvoiceId, afipError, CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.ArcaRejected, bc.Status);
        Assert.Equal(afipError, bc.ArcaErrorMessage);
    }

    // =========================================================================
    // FiscalSnapshot canonicalization
    // =========================================================================

    [Fact]
    public async Task ConfirmAsync_PersisteFiscalSnapshotCanonico()
    {
        var (service, ctx, _, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test snapshot canonico"),
            "user-vendor", null, CancellationToken.None);

        // Mandamos formatos heterogeneos a proposito.
        var request = new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ars",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.BNA_Mayorista,
                ManualJustification: null,
                AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "Consumidor Final"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

        await service.ConfirmAsync(draft.PublicId, request, "user-vendor", null, false, CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal("ARS", bc.FiscalSnapshot.CurrencyAtEvent);
        Assert.Equal("MONOTRIBUTISTA", bc.FiscalSnapshot.AgencyTaxConditionAtEvent);
        Assert.Equal("RESPONSABLE_INSCRIPTO", bc.FiscalSnapshot.SupplierTaxConditionAtEvent);
        Assert.Equal("CONSUMIDOR_FINAL", bc.FiscalSnapshot.CustomerTaxConditionAtEvent);
    }

    [Fact]
    public async Task ConfirmAsync_PasaApprovalRequestIdEnEnqueueAnnulment_CrossReference()
    {
        var (service, ctx, invoiceMock, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test cross-ref"),
            "user-admin", "Admin", CancellationToken.None);

        var bcEntity = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        var approval = await SeedApprovedOverrideAsync(ctx, bcEntity.Id, "user-admin");

        await service.ConfirmAsync(
            draft.PublicId,
            BuildValidConfirm(isOverride: true, approvalPublicId: approval.PublicId),
            "user-admin", "Admin", true, CancellationToken.None);

        invoiceMock.Verify(
            s => s.EnqueueAnnulmentAsync(
                seed.InvoiceId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>(),
                approval.Id),
            Times.Once);
    }
}
