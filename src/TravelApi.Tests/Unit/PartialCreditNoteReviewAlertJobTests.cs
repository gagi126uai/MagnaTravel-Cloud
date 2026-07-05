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
/// FC1.3.6 (ADR-009 §2.10 + plan tactico FC1.3 §FC1.3.6, 2026-05-21): tests
/// unit puros del <see cref="PartialCreditNoteReviewAlertJob"/>.
///
/// <para>
/// <b>Por que unit puros y no integration</b>: misma regla operativa que el
/// resto de FC1.3 — <c>CustomWebApplicationFactory</c> +
/// <c>PostgresIntegrationFixture</c> cuelgan al reviewer. Estos tests usan
/// <c>UseInMemoryDatabase</c> + <c>Moq</c> y corren en milisegundos.
/// </para>
///
/// <para>
/// <b>Que cubren estos 3 tests</b>:
/// <list type="number">
///   <item>BC stale (> alertDays en ManualReviewPending) -> log warning +
///   notification creada para cada admin.</item>
///   <item>BC reciente (< alertDays) -> NO notifica.</item>
///   <item>Sin BCs stale o con modulo apagado -> no-op silencioso.</item>
/// </list>
/// </para>
/// </summary>
public class PartialCreditNoteReviewAlertJobTests
{
    // ============================================================
    // Helpers de armado
    // ============================================================

    /// <summary>
    /// Construye un AppDbContext InMemory con DB unica por test (xUnit corre
    /// en paralelo y un nombre compartido pisaria datos entre tests).
    /// </summary>
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc136-job-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Builder del job con todas las deps mockeadas. Devuelve los mocks/ctx
    /// para que cada test pueda configurar/verificar lo que necesite.
    ///
    /// <para><b>UserManager truco</b>: <see cref="UserManager{T}"/> es muy
    /// dificil de mockear directo (constructor con 9 params + DbContext de
    /// Identity). Pero los metodos que necesitamos (<c>GetUsersInRoleAsync</c>)
    /// son <c>virtual</c>, asi que Moq puede interceptarlos si pasamos un
    /// <see cref="Mock{T}.Object"/> creado sobre un store mockeado. El store
    /// nunca se invoca porque el setup lambda toma precedencia.</para>
    /// </summary>
    private static (
        PartialCreditNoteReviewAlertJob Job,
        AppDbContext Ctx,
        Mock<INotificationService> NotificationMock,
        Mock<UserManager<ApplicationUser>> UserManagerMock,
        OperationalFinanceSettings Settings
    ) BuildJob(
        bool enablePartialCreditNotes = true,
        int alertDays = 10)
    {
        var ctx = NewDbContext();

        var settings = new OperationalFinanceSettings
        {
            EnablePartialCreditNotes = enablePartialCreditNotes,
            ManualReviewMaxDaysBeforeRg4540Alert = alertDays,
        };

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var notificationMock = new Mock<INotificationService>();

        // UserManager mockeado: el store no se usa porque sobreescribimos
        // GetUsersInRoleAsync via Setup.
        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object,
            null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        var job = new PartialCreditNoteReviewAlertJob(
            ctx,
            settingsMock.Object,
            notificationMock.Object,
            userManagerMock.Object,
            NullLogger<PartialCreditNoteReviewAlertJob>.Instance);

        return (job, ctx, notificationMock, userManagerMock, settings);
    }

    /// <summary>
    /// Inserta una factura, una reserva y un BC en
    /// <see cref="BookingCancellationStatus.ManualReviewPending"/> con el
    /// timestamp T1 (<c>ConfirmedWithClientAt</c>) configurable.
    /// </summary>
    private static async Task<BookingCancellation> SeedBookingCancellationAsync(
        AppDbContext ctx,
        BookingCancellationStatus status,
        DateTime? confirmedWithClientAt,
        int bcId = 100)
    {
        // Factura minima — campos requeridos por el modelo.
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

        // Reserva minima (solo lo que el job lee: NumeroReserva).
        // Necesita un CustomerId, no podemos dejar 0 si hay validacion FK,
        // pero InMemory no enforza FK. Asi que solo seteamos lo que se lee.
        var reserva = new Reserva
        {
            Id = bcId,
            NumeroReserva = $"R-2026-{bcId}",
        };
        ctx.Reservas.Add(reserva);

        var bookingCancellation = new BookingCancellation
        {
            Id = bcId,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            CustomerId = 1,
            SupplierId = 1,
            OriginatingInvoiceId = invoice.Id,
            Status = status,
            Reason = "Test FC1.3.6",
            DraftedAt = DateTime.UtcNow.AddDays(-15),
            ConfirmedWithClientAt = confirmedWithClientAt,
            DraftedByUserId = "vendedor-1",
            // El service NO accede a FiscalSnapshot en este job, pero EF requiere
            // que la owned property no sea null al persistir.
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.Add(bookingCancellation);

        await ctx.SaveChangesAsync();
        return bookingCancellation;
    }

    /// <summary>Helper para crear una lista de admins mock.</summary>
    private static IList<ApplicationUser> AdminList(params string[] adminIds) =>
        adminIds.Select(id => new ApplicationUser { Id = id, UserName = $"admin-{id}" }).ToList();

    // ============================================================
    // 1) BC stale por mas del threshold -> log + notification
    // ============================================================

    [Fact]
    public async Task RunAsync_BcStaleByMoreThanAlertThreshold_NotifiesAllAdmins()
    {
        var (job, ctx, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: true,
            alertDays: 10);

        // BC confirmado hace 11 dias (mas que los 10 del threshold) -> stale.
        var staleConfirmedAt = DateTime.UtcNow.AddDays(-11);
        await SeedBookingCancellationAsync(
            ctx,
            BookingCancellationStatus.ManualReviewPending,
            confirmedWithClientAt: staleConfirmedAt);

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1", "admin-2"));

        await job.RunAsync(CancellationToken.None);

        // Una notificacion por admin (2 admins -> 2 notifications).
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif =>
                notif.RelatedEntityType == "PartialCreditNoteReviewPending"
                && notif.Type == "Warning"
                && notif.Priority == "Urgent"),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Verificamos que cada admin recibio su notificacion (no se la mandamos
        // dos veces al mismo).
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif => notif.UserId == "admin-1"),
            It.IsAny<CancellationToken>()),
            Times.Once);
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif => notif.UserId == "admin-2"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 2) BC dentro del threshold -> NO notifica
    // ============================================================

    [Fact]
    public async Task RunAsync_BcWithinAlertThreshold_DoesNotNotify()
    {
        var (job, ctx, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: true,
            alertDays: 10);

        // BC confirmado hace 5 dias (menos que los 10 del threshold) -> fresco.
        var freshConfirmedAt = DateTime.UtcNow.AddDays(-5);
        await SeedBookingCancellationAsync(
            ctx,
            BookingCancellationStatus.ManualReviewPending,
            confirmedWithClientAt: freshConfirmedAt);

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1"));

        await job.RunAsync(CancellationToken.None);

        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.IsAny<Notification>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 3) Sin BCs en ManualReviewPending -> no-op silencioso
    // ============================================================

    [Fact]
    public async Task RunAsync_NoBcsInManualReviewPending_NoOp()
    {
        var (job, ctx, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: true,
            alertDays: 10);

        // BC en otro estado (AwaitingFiscalConfirmation) — aunque sea viejo,
        // NO debe disparar alerta porque no esta esperando revision manual.
        await SeedBookingCancellationAsync(
            ctx,
            BookingCancellationStatus.AwaitingFiscalConfirmation,
            confirmedWithClientAt: DateTime.UtcNow.AddDays(-30));

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1"));

        await job.RunAsync(CancellationToken.None);

        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.IsAny<Notification>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 4) Modulo FC1.3 apagado -> no-op (no llama ni a GetUsersInRoleAsync)
    // ============================================================

    [Fact]
    public async Task RunAsync_EnablePartialCreditNotesFalse_NoOp()
    {
        var (job, ctx, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: false,
            alertDays: 10);

        // Aunque haya un BC stale, el flag apagado lo deja pasar.
        await SeedBookingCancellationAsync(
            ctx,
            BookingCancellationStatus.ManualReviewPending,
            confirmedWithClientAt: DateTime.UtcNow.AddDays(-20));

        await job.RunAsync(CancellationToken.None);

        // No notifico, y tampoco fue a buscar usuarios (early-return antes).
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.IsAny<Notification>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        userManagerMock.Verify(u => u.GetUsersInRoleAsync(It.IsAny<string>()),
            Times.Never);
    }

    // ============================================================
    // 5) Dedup (Tanda 5): si ya hay un aviso VIVO con la misma clave, no notifico de nuevo
    // ============================================================

    [Fact]
    public async Task RunAsync_AlreadyHasLiveAlert_DoesNotDuplicate()
    {
        var (job, ctx, notificationMock, userManagerMock, _) = BuildJob(
            enablePartialCreditNotes: true,
            alertDays: 10);

        var bookingCancellation = await SeedBookingCancellationAsync(
            ctx,
            BookingCancellationStatus.ManualReviewPending,
            confirmedWithClientAt: DateTime.UtcNow.AddDays(-11));

        // Pre-existente: ya hay un aviso VIVO para admin-1 sobre este BC (misma clave de resolucion). El dedup ahora
        // mira "vivo con esta clave", no "creado hoy": mientras siga vivo no se re-crea (aunque pasen dias).
        ctx.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            RelatedEntityType = "PartialCreditNoteReviewPending",
            RelatedEntityId = bookingCancellation.Id,
            ResolutionKey = NotificationResolutionKeys.ForEntity(
                "PartialCreditNoteReviewPending", bookingCancellation.Id),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            Message = "(seed) ya notificado",
            Type = "Warning",
            Priority = "Urgent",
        });
        await ctx.SaveChangesAsync();

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1"));

        await job.RunAsync(CancellationToken.None);

        // No se creo una segunda notification para admin-1 sobre este BC hoy.
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.IsAny<Notification>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
