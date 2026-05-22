using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.6b (ADR-009 §2.12 round 3 + plan tactico FC1.3 §FC1.3.6b, 2026-05-21):
/// tests unit puros del <see cref="PartialCreditNoteBridgeReconciliationJob"/>.
///
/// <para><b>Por que unit puros y no integration</b>: misma regla operativa que
/// el resto de FC1.3 — <c>CustomWebApplicationFactory</c> +
/// <c>PostgresIntegrationFixture</c> cuelgan al reviewer. Estos tests usan
/// <c>UseInMemoryDatabase</c> + <c>Moq</c> y corren en milisegundos.</para>
///
/// <para><b>Que cubren los 5 tests</b>:
/// <list type="number">
///   <item>AR Approved + BC ManualReviewPending stale -> bridge.OnApprovedAsync
///   invocado, BridgeLastAttemptAt seteado, BridgeLastError null.</item>
///   <item>AR Approved hace 10 min con staleness=30 -> bridge NUNCA invocado.</item>
///   <item>Bridge tira siempre. Tras 5 ejecuciones: counter=5, error poblado,
///   NotifyAdmins invocado EXACTAMENTE 1 vez (al llegar al limite).
///   Ejecuciones 6 y 7 no llaman al bridge ni notifican.</item>
///   <item>BC ya transiciono a ManualReviewApproved -> bridge NUNCA invocado
///   (no es huerfano).</item>
///   <item>EnablePartialCreditNotes=false -> early return.</item>
/// </list>
/// </para>
/// </summary>
public class PartialCreditNoteBridgeReconciliationJobTests
{
    // ============================================================
    // Helpers de armado
    // ============================================================

    /// <summary>
    /// AppDbContext InMemory con DB unica por test. xUnit corre en paralelo;
    /// usar nombre compartido pisa datos entre tests.
    /// </summary>
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc136b-job-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Builder del job. Devuelve el job + ctx + mocks para que cada test
    /// configure / verifique lo que necesita.
    /// </summary>
    private static (
        PartialCreditNoteBridgeReconciliationJob Job,
        AppDbContext Ctx,
        Mock<IPartialCreditNoteApprovalBridge> BridgeMock,
        Mock<INotificationService> NotificationMock,
        Mock<UserManager<ApplicationUser>> UserManagerMock,
        OperationalFinanceSettings Settings
    ) BuildJob(
        bool enablePartialCreditNotes = true,
        int stalenessMinutes = 30,
        int maxRetries = 5)
    {
        var ctx = NewDbContext();

        var settings = new OperationalFinanceSettings
        {
            EnablePartialCreditNotes = enablePartialCreditNotes,
            BridgeReconciliationStalenessMinutes = stalenessMinutes,
            BridgeReconciliationMaxRetries = maxRetries,
        };

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var bridgeMock = new Mock<IPartialCreditNoteApprovalBridge>();
        var notificationMock = new Mock<INotificationService>();

        // UserManager: el store no se usa porque sobreescribimos
        // GetUsersInRoleAsync via Setup. Mismo patron que FC1.3.6 tests.
        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object,
            null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        var job = new PartialCreditNoteBridgeReconciliationJob(
            ctx,
            settingsMock.Object,
            bridgeMock.Object,
            notificationMock.Object,
            userManagerMock.Object,
            NullLogger<PartialCreditNoteBridgeReconciliationJob>.Instance);

        return (job, ctx, bridgeMock, notificationMock, userManagerMock, settings);
    }

    /// <summary>
    /// Crea un ApprovalRequest tipo PartialCreditNoteApproval en el estado y
    /// con la antiguedad dada. Tambien crea (si bcStatus != null) el BC
    /// asociado con FK al approval.
    /// </summary>
    private static async Task<(ApprovalRequest Ar, BookingCancellation? Bc)> SeedAsync(
        AppDbContext ctx,
        ApprovalStatus arStatus,
        DateTime arResolvedAt,
        BookingCancellationStatus? bcStatus,
        int arId = 100,
        int bcId = 200,
        int bridgeRetryCount = 0)
    {
        var ar = new ApprovalRequest
        {
            Id = arId,
            PublicId = Guid.NewGuid(),
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            RequestedByUserId = "vendedor-1",
            RequestedAt = arResolvedAt.AddMinutes(-1),
            EntityType = "BookingCancellation",
            EntityId = bcId,
            Status = arStatus,
            ResolvedByUserId = "admin-1",
            ResolvedByUserName = "Admin 1",
            ResolvedAt = arResolvedAt,
            ResolverNotes = "Aprobacion valida con comentario suficiente para pasar la validacion del bridge.",
            ExpiresAt = DateTime.UtcNow.AddDays(5),
            BridgeRetryCount = bridgeRetryCount,
        };
        ctx.ApprovalRequests.Add(ar);

        BookingCancellation? bc = null;
        if (bcStatus.HasValue)
        {
            // Factura minima — el job NO la usa pero EF puede requerirla por FK.
            var invoice = new Invoice
            {
                Id = bcId * 10,
                TipoComprobante = 1,
                PuntoDeVenta = 7,
                NumeroComprobante = bcId,
                ImporteTotal = 100_000m,
                ImporteNeto = 82_645m,
                ImporteIva = 17_355m,
            };
            ctx.Invoices.Add(invoice);

            var reserva = new Reserva
            {
                Id = bcId,
                NumeroReserva = $"R-2026-{bcId}",
            };
            ctx.Reservas.Add(reserva);

            bc = new BookingCancellation
            {
                Id = bcId,
                PublicId = Guid.NewGuid(),
                ReservaId = reserva.Id,
                CustomerId = 1,
                SupplierId = 1,
                OriginatingInvoiceId = invoice.Id,
                Status = bcStatus.Value,
                Reason = "Test FC1.3.6b",
                DraftedAt = DateTime.UtcNow.AddDays(-10),
                DraftedByUserId = "vendedor-1",
                FiscalSnapshot = new FiscalSnapshot(),
                PartialCreditNoteApprovalRequestId = ar.Id,
            };
            ctx.BookingCancellations.Add(bc);
        }

        await ctx.SaveChangesAsync();
        return (ar, bc);
    }

    /// <summary>Helper: lista de admins fake para mock del UserManager.</summary>
    private static IList<ApplicationUser> AdminList(params string[] ids) =>
        ids.Select(id => new ApplicationUser { Id = id, UserName = $"admin-{id}" }).ToList();

    // ============================================================
    // 1) AR Approved + BC ManualReviewPending stale -> bridge invocado
    // ============================================================

    [Fact]
    public async Task RunAsync_OrphanWithApprovedAR_ForcesCallback()
    {
        var (job, ctx, bridgeMock, _, _, _) = BuildJob(
            enablePartialCreditNotes: true,
            stalenessMinutes: 30,
            maxRetries: 5);

        // AR resuelta hace 40 min (mas que los 30 del threshold) + BC huerfano.
        var resolvedAt = DateTime.UtcNow.AddMinutes(-40);
        var (ar, _) = await SeedAsync(
            ctx,
            arStatus: ApprovalStatus.Approved,
            arResolvedAt: resolvedAt,
            bcStatus: BookingCancellationStatus.ManualReviewPending);

        await job.RunAsync(CancellationToken.None);

        // Bridge fue invocado con OnApprovedAsync (Status=Approved).
        bridgeMock.Verify(b => b.OnApprovedAsync(
            ar.Id,
            "admin-1",
            "Admin 1",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // BridgeLastAttemptAt seteado, BridgeLastError limpiado.
        var arPost = await ctx.ApprovalRequests.FirstAsync(a => a.Id == ar.Id);
        Assert.NotNull(arPost.BridgeLastAttemptAt);
        Assert.Null(arPost.BridgeLastError);
        // El counter NO se incrementa en exito (decision de diseño).
        Assert.Equal(0, arPost.BridgeRetryCount);
    }

    // ============================================================
    // 2) AR Approved fresh (dentro del staleness) -> NO llama al bridge
    // ============================================================

    [Fact]
    public async Task RunAsync_NotStaleEnough_DoesNotForceCallback()
    {
        var (job, ctx, bridgeMock, _, _, _) = BuildJob(
            enablePartialCreditNotes: true,
            stalenessMinutes: 30,
            maxRetries: 5);

        // AR resuelta hace 10 min (menos que los 30 del threshold) -> fresca.
        var resolvedAt = DateTime.UtcNow.AddMinutes(-10);
        await SeedAsync(
            ctx,
            arStatus: ApprovalStatus.Approved,
            arResolvedAt: resolvedAt,
            bcStatus: BookingCancellationStatus.ManualReviewPending);

        await job.RunAsync(CancellationToken.None);

        bridgeMock.Verify(b => b.OnApprovedAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        bridgeMock.Verify(b => b.OnRejectedAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 3) Falla persistente: counter sube, notificacion 1 sola vez al limite
    // ============================================================

    [Fact]
    public async Task RunAsync_PersistentFailure_DoesNotSpamNotifications()
    {
        var (job, ctx, bridgeMock, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: true,
            stalenessMinutes: 30,
            maxRetries: 5);

        // Bridge tira excepcion siempre.
        bridgeMock.Setup(b => b.OnApprovedAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Bridge roto: ejemplo de error persistente"));

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1", "admin-2"));

        // AR resuelta hace 40 min, BC en ManualReviewPending.
        var resolvedAt = DateTime.UtcNow.AddMinutes(-40);
        var (ar, _) = await SeedAsync(
            ctx,
            arStatus: ApprovalStatus.Approved,
            arResolvedAt: resolvedAt,
            bcStatus: BookingCancellationStatus.ManualReviewPending);

        // Ejecutar el job 7 veces seguidas (5 entran + 2 ya filtran).
        for (int i = 0; i < 7; i++)
        {
            await job.RunAsync(CancellationToken.None);
        }

        // Bridge solo se invoca 5 veces (las primeras 5; las ultimas 2 quedan
        // afuera de la query porque BridgeRetryCount >= maxRetries).
        bridgeMock.Verify(b => b.OnApprovedAsync(
            ar.Id,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Exactly(5));

        // Counter llega a 5 y se queda.
        var arPost = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == ar.Id);
        Assert.Equal(5, arPost.BridgeRetryCount);
        Assert.NotNull(arPost.BridgeLastError);
        Assert.Contains("Bridge roto", arPost.BridgeLastError);

        // Notificacion al admin: UNA por cada admin, UNA sola vez (al llegar a 5).
        // 2 admins -> 2 notifications total. La key del anti-spam es que en las
        // ejecuciones 6 y 7 NO se crean mas notifications (el counter ya esta
        // en el limite y la AR no entra a la query).
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif =>
                notif.RelatedEntityType == "PartialCreditNoteBridgeReconciliationFailed"
                && notif.Priority == "Urgent"),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ============================================================
    // 4) BC ya transiciono -> AR no es huerfana, no se llama al bridge
    // ============================================================

    [Fact]
    public async Task RunAsync_BcAlreadyTransitioned_SkipsArWithoutCallback()
    {
        var (job, ctx, bridgeMock, _, _, _) = BuildJob(
            enablePartialCreditNotes: true,
            stalenessMinutes: 30,
            maxRetries: 5);

        // AR vieja pero BC ya esta en ManualReviewApproved -> no es huerfana.
        var resolvedAt = DateTime.UtcNow.AddMinutes(-40);
        await SeedAsync(
            ctx,
            arStatus: ApprovalStatus.Approved,
            arResolvedAt: resolvedAt,
            bcStatus: BookingCancellationStatus.ManualReviewApproved);

        await job.RunAsync(CancellationToken.None);

        bridgeMock.Verify(b => b.OnApprovedAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 5) Modulo apagado -> early return (no toca ni la query principal)
    // ============================================================

    [Fact]
    public async Task RunAsync_FlagOff_NoOp()
    {
        var (job, ctx, bridgeMock, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: false,
            stalenessMinutes: 30,
            maxRetries: 5);

        // Aunque haya AR huerfana stale, el flag apagado lo deja pasar.
        var resolvedAt = DateTime.UtcNow.AddMinutes(-40);
        await SeedAsync(
            ctx,
            arStatus: ApprovalStatus.Approved,
            arResolvedAt: resolvedAt,
            bcStatus: BookingCancellationStatus.ManualReviewPending);

        await job.RunAsync(CancellationToken.None);

        bridgeMock.Verify(b => b.OnApprovedAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.IsAny<Notification>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        userManagerMock.Verify(u => u.GetUsersInRoleAsync(It.IsAny<string>()),
            Times.Never);
    }
}
