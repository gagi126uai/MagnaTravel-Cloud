using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.2 reviewer pass (2026-05-18): tests que cubren los bugs detectados en
/// el review de FC1.2.1 / FC1.2.2 sobre <see cref="BookingCancellationService"/>:
/// <list type="bullet">
///   <item>T2 — F5: ConfirmAsync sin override NO bypasea el approval del InvoiceAnnulment.</item>
///   <item>T6 — Postgres real: void de la ultima allocation revierte el BC status.</item>
///   <item>T7 — OnArcaSucceeded sin BC matchante NO tira excepcion (no-op + warning).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BookingCancellationServiceFixesTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public BookingCancellationServiceFixesTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // T2 — Sin override admin, ConfirmAsync NO bypasea el approval del
    //      InvoiceAnnulment (pasa requesterIsAdmin=false a InvoiceService).
    // =========================================================================

    /// <summary>
    /// Bug F5: el codigo hardcodeaba <c>requesterIsAdmin: true</c> en la llamada
    /// a <c>EnqueueAnnulmentAsync</c>, lo que dejaba al InvoiceService saltearse
    /// su propio workflow de approval. La regla correcta (OPS-FISCAL-001 plan v3
    /// §13) es: el bypass es valido SOLO cuando el BC trae un InvariantOverride
    /// aprobado. Sin override -> requesterIsAdmin=false -> approval normal.
    /// </summary>
    [Fact]
    public async Task Confirm_SinAdminOverride_NoBypaseaApprovalDelInvoiceAnnulment()
    {
        var (service, ctx, invoiceMock, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Cliente arrepentido, sin override admin"),
            "user-vendor", "Vendor", CancellationToken.None);

        // ConfirmAsync sin override. requesterIsAdmin del parametro lo
        // mandamos en false para reflejar que es un vendedor normal.
        await service.ConfirmAsync(
            draft.PublicId,
            BuildValidConfirm(isOverride: false),
            "user-vendor", "Vendor",
            requesterIsAdmin: false,
            CancellationToken.None);

        // Verificacion CORE: EnqueueAnnulmentAsync recibio requesterIsAdmin=false
        // (NO el true hardcodeado del bug F5). Si F5 esta revertido, este Verify
        // falla con "Expected ... requesterIsAdmin=false but was true".
        invoiceMock.Verify(
            s => s.EnqueueAnnulmentAsync(
                seed.InvoiceId,
                "user-vendor",
                "Vendor",
                It.IsAny<string>(),
                false, // <-- la verificacion clave del F5.
                It.IsAny<CancellationToken>(),
                null), // approvalRequestId null cuando no hay override.
            Times.Once);
    }

    /// <summary>
    /// Caso espejo del T2: con override aprobado, SI se debe bypasar el approval
    /// (requesterIsAdmin=true + approvalRequestId del override). Esto es el path
    /// "OK" — sin este test, F5 podria romper el escenario de override sin que
    /// nadie se entere.
    /// </summary>
    [Fact]
    public async Task Confirm_ConOverrideAprobado_BypaseaApprovalDelInvoiceAnnulment()
    {
        var (service, ctx, invoiceMock, _) = BuildService();
        var seed = await SeedScenarioAsync(ctx);

        var draft = await service.DraftAsync(
            new DraftCancellationRequest(seed.ReservaPublicId, "Test override aprobado"),
            "user-admin", "Admin", CancellationToken.None);

        var bcEntity = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        var approval = await SeedApprovedOverrideAsync(ctx, bcEntity.Id, "user-admin");

        await service.ConfirmAsync(
            draft.PublicId,
            BuildValidConfirm(isOverride: true, approvalPublicId: approval.PublicId),
            "user-admin", "Admin",
            requesterIsAdmin: true,
            CancellationToken.None);

        // Con override SI bypassa (requesterIsAdmin=true) y pasa approvalId.
        invoiceMock.Verify(
            s => s.EnqueueAnnulmentAsync(
                seed.InvoiceId,
                "user-admin",
                "Admin",
                It.IsAny<string>(),
                true, // bypass valido porque hay approval.
                It.IsAny<CancellationToken>(),
                approval.Id),
            Times.Once);
    }

    // =========================================================================
    // T6 — Void de la ultima allocation revierte BC a AwaitingOperatorRefund
    //      contra Postgres real (validar que EF/CountAsync ve el cambio
    //      in-memory de IsVoided=true).
    // =========================================================================

    /// <summary>
    /// Bug observation O9: OnAllocationVoidedAsync usa <c>CountAsync</c> sobre
    /// el DbSet para contar allocations activas restantes. En InMemory el
    /// ChangeTracker "ve" el IsVoided=true recien seteado, pero en Postgres
    /// real con EF8 esto depende del comportamiento del query — si EF traduce
    /// a SQL directo, NO ve el cambio in-memory.
    ///
    /// Test contra Postgres real: void de la unica allocation activa. Esperado:
    /// el BC vuelve a AwaitingOperatorRefund (la query veo 0 allocations activas).
    /// </summary>
    [Fact]
    public async Task OnAllocationVoided_UltimaAllocation_RevierteBcStatus_EnPostgresReal()
    {
        var (refundPublicId, bcPublicId) = await SeedAllocationReadyScenarioAsync(receivedAmount: 1000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var refundSvc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var alloc = await refundSvc.AllocateAsync(
            refundPublicId,
            new AllocateRefundRequest(bcPublicId, GrossAmount: 500m, new List<DeductionLineRequest>()),
            "user", null, CancellationToken.None);

        // Pre: BC esta en ClientCreditApplied (post-primera allocation).
        await using (var ctxPre = _fixture.CreateDbContext())
        {
            var bcPre = await ctxPre.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bcPre.Status);
        }

        // Void.
        await refundSvc.VoidAllocationAsync(
            alloc.PublicId,
            new VoidAllocationRequest("Anulamos la unica allocation activa del BC"),
            "user", null, CancellationToken.None);

        // Post: el BC debe haber vuelto a AwaitingOperatorRefund. Si EF8 con
        // Postgres real NO ve el IsVoided=true in-memory en el CountAsync, el
        // codigo cuenta 1 allocation activa (la voided) y NO transiciona —
        // el assert falla y reportamos el bug subyacente.
        await using var ctx = _fixture.CreateDbContext();
        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        Assert.Equal(0m, bc.ReceivedRefundAmount);

        var refund = await ctx.OperatorRefundReceived.AsNoTracking().FirstAsync(r => r.PublicId == refundPublicId);
        Assert.Equal(0m, refund.AllocatedAmount);
    }

    // =========================================================================
    // T7 — OnArcaSucceeded sin BC matchante: warning + no-op (no tira).
    // =========================================================================

    /// <summary>
    /// Defensivo: si el bridge recibe un OnArcaSucceededAsync para una invoice
    /// cuyo BC fue borrado (o nunca existio), el service NO debe tirar — debe
    /// loguear warning y seguir. Si tirara, Hangfire reintentaria el job y
    /// llamaria a AFIP de nuevo (NC duplicada).
    /// </summary>
    [Fact]
    public async Task OnArcaSucceeded_BcNoEncontrado_NoTira_LogeaWarning()
    {
        // No seedeamos ningun BC: el callback va a buscar por OriginatingInvoiceId=9999
        // y no encontrar nada.
        var capturingLogger = new CapturingLogger<BookingCancellationService>();
        var (service, _, _, _) = BuildService(loggerOverride: capturingLogger);

        // Llamar al bridge directo. No debe tirar excepcion.
        var bridge = (IInvoiceAnnulmentBcBridge)service;

        // Si tira, el test falla; si retorna OK, validamos que se logueo warning.
        await bridge.OnArcaSucceededAsync(
            originatingInvoiceId: 9999,
            creditNoteInvoiceId: 8888,
            CancellationToken.None);

        // Verificamos que se haya logueado al menos un warning con "no se encontro BC".
        var hasWarning = capturingLogger.Records.Any(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("no se encontro BC", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasWarning,
            $"Esperaba warning con 'no se encontro BC' pero capturamos: " +
            $"{string.Join(" | ", capturingLogger.Records.Select(r => $"{r.Level}: {r.Message}"))}");
    }

    // =========================================================================
    // Helpers (replican el patron de BookingCancellationServiceTests).
    // =========================================================================

    private (BookingCancellationService service,
             AppDbContext ctx,
             Mock<IInvoiceService> invoiceMock,
             Mock<IOperationalFinanceSettingsService> settingsMock)
        BuildService(AppDbContext? ctxIn = null, bool featureFlagOn = true, ILogger<BookingCancellationService>? loggerOverride = null)
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
        // Mocks vacios sirven porque estos tests no prenden EnablePartialCreditNotes.
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalService,
            auditMock.Object,
            loggerOverride ?? NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return (service, ctx, invoiceMock, settingsMock);
    }

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

    private static async Task<ApprovalRequest> SeedApprovedOverrideAsync(
        AppDbContext ctx, int bcId, string userId)
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

    /// <summary>
    /// Setup minimo para T6: 1 BC en AwaitingOperatorRefund + 1 refund listo
    /// para allocate. Igual al patron de OperatorRefundServiceTests.SeedFullScenarioAsync.
    /// </summary>
    private async Task<(Guid RefundPublicId, Guid BcPublicId)> SeedAllocationReadyScenarioAsync(decimal receivedAmount)
    {
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId, BookingCancellationStatus.AwaitingOperatorRefund);
        bc.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc.FiscalSnapshot.SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc.FiscalSnapshot.CurrencyAtEvent = "ARS";
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAmount = receivedAmount,
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

        return (refund.PublicId, bc.PublicId);
    }
}

/// <summary>
/// Logger de prueba que captura las entradas en memoria para que los tests
/// puedan inspeccionarlas. No depende de Microsoft.Extensions.Logging.Testing
/// (que no esta en este csproj). Thread-safe basico via lock.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate) return _records.ToArray();
        }
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_gate)
        {
            _records.Add(new LogRecord(logLevel, message, exception));
        }
    }
}

internal sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);
