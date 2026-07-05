using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Notifications;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (Tanda 5, 2026-07-05) Auto-resolución de notificaciones: semántica única "vivo", ResolveByKeyAsync idempotente,
/// dedup entre días por aviso vivo, W4 (apagado de zombies) y las reglas puras de causa muerta. InMemory + Moq.
/// </summary>
public class NotificationResolutionTests
{
    // ============================================================
    // Armado
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"notif-resolution-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>Dispatcher no-op: los tests no necesitan SignalR real.</summary>
    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static NotificationService NewNotificationService(AppDbContext ctx)
        => new NotificationService(new Repository<Notification>(ctx), new NoopDispatcher());

    private static Notification LiveNotification(string userId, string type, string relatedType, int relatedId, string priority = "Normal")
        => new()
        {
            UserId = userId,
            Message = $"aviso {type} {relatedType} {relatedId}",
            Type = type,
            Priority = priority,
            RelatedEntityType = relatedType,
            RelatedEntityId = relatedId,
        };

    // ============================================================
    // A) NotificationService: semántica "vivo", ambos flags, auto-key, ResolveByKey.
    // ============================================================

    [Fact]
    public async Task Dismiss_MarksBothFlags_AndDisappearsFromBothQueries()
    {
        var ctx = NewDbContext();
        var service = NewNotificationService(ctx);

        var created = await service.CreateAndSendAsync(
            LiveNotification("user-1", "Warning", "Invoice", 10, priority: "Urgent"));

        // Antes de descartar aparece en AMBAS vistas (campanita y banner urgente).
        Assert.Single(await service.GetUnreadNotificationsAsync("user-1", CancellationToken.None));
        Assert.Single(await service.GetUrgentNotificationsAsync("user-1", CancellationToken.None));

        var ok = await service.DismissAsync(created.Id, "user-1", CancellationToken.None);
        Assert.True(ok);

        // Descartar setea AMBOS flags (para un usuario "ya la vi" es una sola cosa).
        var stored = await ctx.Notifications.AsNoTracking().FirstAsync(n => n.Id == created.Id);
        Assert.True(stored.IsRead);
        Assert.True(stored.IsDismissed);

        // Y desaparece de las DOS vistas (antes descartar el banner no sacaba el punto de la campanita).
        Assert.Empty(await service.GetUnreadNotificationsAsync("user-1", CancellationToken.None));
        Assert.Empty(await service.GetUrgentNotificationsAsync("user-1", CancellationToken.None));
    }

    [Fact]
    public async Task MarkAsRead_AlsoSetsDismissed()
    {
        var ctx = NewDbContext();
        var service = NewNotificationService(ctx);
        var created = await service.CreateAndSendAsync(LiveNotification("u", "Info", "Invoice", 1));

        await service.MarkAsReadAsync(created.Id, "u", CancellationToken.None);

        var stored = await ctx.Notifications.AsNoTracking().FirstAsync();
        Assert.True(stored.IsRead);
        Assert.True(stored.IsDismissed);
    }

    [Fact]
    public async Task CreateAndSend_DerivesResolutionKeyFromEntity()
    {
        var ctx = NewDbContext();
        var service = NewNotificationService(ctx);

        var created = await service.CreateAndSendAsync(LiveNotification("u", "Error", "Invoice", 42));

        Assert.Equal("Invoice:42", created.ResolutionKey);
    }

    [Fact]
    public async Task ResolveByKey_ResolvesLiveMatching_IsIdempotent_AndLeavesOthers()
    {
        var ctx = NewDbContext();
        var service = NewNotificationService(ctx);

        // Dos avisos vivos con la MISMA clave + uno con otra clave + uno ya leído con la misma clave.
        await service.CreateAndSendAsync(LiveNotification("u1", "Error", "Invoice", 5));
        await service.CreateAndSendAsync(LiveNotification("u2", "Error", "Invoice", 5));
        await service.CreateAndSendAsync(LiveNotification("u1", "Warning", "Invoice", 9)); // otra clave
        var alreadyRead = await service.CreateAndSendAsync(LiveNotification("u3", "Error", "Invoice", 5));
        await service.MarkAsReadAsync(alreadyRead.Id, "u3", CancellationToken.None); // deja de estar vivo

        // Primera resolución: apaga los DOS vivos de "Invoice:5" (no el leído, no la otra clave).
        var resolved = await service.ResolveByKeyAsync("Invoice:5", CancellationToken.None);
        Assert.Equal(2, resolved);

        // Idempotente: segunda vez no hay nada vivo con esa clave.
        var resolvedAgain = await service.ResolveByKeyAsync("Invoice:5", CancellationToken.None);
        Assert.Equal(0, resolvedAgain);

        // La otra clave sigue viva.
        var liveOther = await ctx.Notifications.AsNoTracking()
            .CountAsync(n => n.ResolutionKey == "Invoice:9" && n.ResolvedAt == null);
        Assert.Equal(1, liveOther);

        // El aviso ya leído NO se marca como resuelto (no estaba vivo).
        var readOne = await ctx.Notifications.AsNoTracking().FirstAsync(n => n.Id == alreadyRead.Id);
        Assert.Null(readOne.ResolvedAt);
    }

    [Fact]
    public async Task ResolveByKey_NullOrEmpty_IsNoOp()
    {
        var ctx = NewDbContext();
        var service = NewNotificationService(ctx);

        Assert.Equal(0, await service.ResolveByKeyAsync(null, CancellationToken.None));
        Assert.Equal(0, await service.ResolveByKeyAsync("   ", CancellationToken.None));
    }

    // ============================================================
    // B) Reglas puras de causa muerta (NotificationCauseResolutionRules).
    // ============================================================

    [Fact]
    public void Rule_ReservaNeedsReview_ResolvedWhenMarkCleared()
    {
        var notif = LiveNotification("u", NotificationTypes.ReservaNeedsReview, NotificationRelatedEntityTypes.Reserva, 1);

        var stillMarked = new NotificationCauseResolutionRules.ReservaCauseState(EstadoReserva.Confirmed, 0m, HasUnacknowledgedChanges: true);
        var cleared = new NotificationCauseResolutionRules.ReservaCauseState(EstadoReserva.Confirmed, 0m, HasUnacknowledgedChanges: false);

        Assert.False(NotificationCauseResolutionRules.IsCauseResolved(notif, stillMarked, null));
        Assert.True(NotificationCauseResolutionRules.IsCauseResolved(notif, cleared, null));
        Assert.True(NotificationCauseResolutionRules.IsCauseResolved(notif, null, null)); // reserva borrada
    }

    [Fact]
    public void Rule_UnpaidDeparture_ResolvedWhenSettledOrTerminal()
    {
        var notif = LiveNotification("u", "Warning", NotificationRelatedEntityTypes.ReservaUnpaidDeparture, 1);

        var owing = new NotificationCauseResolutionRules.ReservaCauseState(EstadoReserva.Confirmed, 500m, false);
        var settled = new NotificationCauseResolutionRules.ReservaCauseState(EstadoReserva.Confirmed, 0m, false);
        var terminal = new NotificationCauseResolutionRules.ReservaCauseState(EstadoReserva.Cancelled, 500m, false);

        Assert.False(NotificationCauseResolutionRules.IsCauseResolved(notif, owing, null));
        Assert.True(NotificationCauseResolutionRules.IsCauseResolved(notif, settled, null));
        Assert.True(NotificationCauseResolutionRules.IsCauseResolved(notif, terminal, null));
    }

    [Fact]
    public void Rule_AnnulmentError_ResolvedWhenInvoiceSucceeded()
    {
        var notif = LiveNotification("u", NotificationTypes.Error, NotificationRelatedEntityTypes.Invoice, 1);
        notif.Message = "Error técnico al anular: timeout. Se reintentará automáticamente.";

        var pending = new NotificationCauseResolutionRules.InvoiceCauseState(AnnulmentStatus.Pending);
        var succeeded = new NotificationCauseResolutionRules.InvoiceCauseState(AnnulmentStatus.Succeeded);

        Assert.False(NotificationCauseResolutionRules.IsCauseResolved(notif, null, pending));
        Assert.True(NotificationCauseResolutionRules.IsCauseResolved(notif, null, succeeded));
    }

    [Fact]
    public void Rule_UnhandledType_IsNeverResolved()
    {
        // Un aviso de un tipo sin regla (ej. el resumen del vigía) no lo toca W4.
        var notif = LiveNotification("u", "Warning", "CoherenceWatchdogReport", 0);
        Assert.False(NotificationCauseResolutionRules.IsCauseResolved(notif, null, null));
    }

    // ============================================================
    // C) W4 vía CoherenceChecks: apaga el zombie, respeta el que aún tiene causa.
    // ============================================================

    [Fact]
    public async Task W4_ResolvesZombieNeedsReview_OfAnnulledReserva_ButNotLiveOne()
    {
        var ctx = NewDbContext();

        // Reserva A: anulada y SIN marca (la causa de "confirmada con cambios" ya murió) → su aviso es zombie.
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-A", Name = "Anulada sin marca", Status = EstadoReserva.Cancelled,
            AdultCount = 1, HasUnacknowledgedChanges = false
        });
        // Reserva B: confirmada y CON marca viva → su aviso todavía tiene causa.
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-B", Name = "Con marca viva", Status = EstadoReserva.Confirmed,
            AdultCount = 1, HasUnacknowledgedChanges = true
        });

        var service = NewNotificationService(ctx);
        var zombie = await service.CreateAndSendAsync(new Notification
        {
            UserId = "u", Type = NotificationTypes.ReservaNeedsReview, Priority = "Urgent",
            RelatedEntityType = NotificationRelatedEntityTypes.Reserva, RelatedEntityId = 1,
            ResolutionKey = NotificationResolutionKeys.ForTyped(NotificationTypes.ReservaNeedsReview, 1),
            Message = "revisar A"
        });
        var stillAlive = await service.CreateAndSendAsync(new Notification
        {
            UserId = "u", Type = NotificationTypes.ReservaNeedsReview, Priority = "Urgent",
            RelatedEntityType = NotificationRelatedEntityTypes.Reserva, RelatedEntityId = 2,
            ResolutionKey = NotificationResolutionKeys.ForTyped(NotificationTypes.ReservaNeedsReview, 2),
            Message = "revisar B"
        });

        var findings = await CoherenceChecks.ResolveZombieNotificationsAsync(ctx, CancellationToken.None);
        await ctx.SaveChangesAsync();

        // Solo el zombie se apagó.
        Assert.Single(findings);
        Assert.Equal("W4", findings[0].Code);

        var zombieStored = await ctx.Notifications.AsNoTracking().FirstAsync(n => n.Id == zombie.Id);
        var aliveStored = await ctx.Notifications.AsNoTracking().FirstAsync(n => n.Id == stillAlive.Id);
        Assert.NotNull(zombieStored.ResolvedAt);
        Assert.Null(aliveStored.ResolvedAt);
    }

    [Fact]
    public async Task W4_ResolvesAnnulmentError_WhenInvoiceAlreadySucceeded()
    {
        var ctx = NewDbContext();

        // Factura ya anulada con éxito, pero quedó vivo un error de anulación previo (el caso D3).
        ctx.Invoices.Add(new Invoice { Id = 77, AnnulmentStatus = AnnulmentStatus.Succeeded });

        var service = NewNotificationService(ctx);
        var error = await service.CreateAndSendAsync(new Notification
        {
            UserId = "u", Type = NotificationTypes.Error, Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.Invoice, RelatedEntityId = 77,
            Message = "Error técnico al anular: timeout. Se reintentará automáticamente."
        });

        var findings = await CoherenceChecks.ResolveZombieNotificationsAsync(ctx, CancellationToken.None);
        await ctx.SaveChangesAsync();

        Assert.Single(findings);
        var stored = await ctx.Notifications.AsNoTracking().FirstAsync(n => n.Id == error.Id);
        Assert.NotNull(stored.ResolvedAt);
    }

    // ============================================================
    // D) Dedup entre días del monitor: no duplica con viva previa, SÍ re-crea tras resolución.
    // ============================================================

    private static Mock<UserManager<ApplicationUser>> BuildUserManagerWithNoAdmins()
    {
        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync((IList<ApplicationUser>)new List<ApplicationUser>());
        return userManagerMock;
    }

    [Fact]
    public async Task Monitor_DoesNotDuplicateWhileLive_ButRecreatesAfterResolutionWithPersistentCause()
    {
        var ctx = NewDbContext();

        // Reserva que debe y sale dentro de la ventana → dispara "sale pronto y debe".
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Debe y viaja pronto",
            Status = EstadoReserva.Confirmed, AdultCount = 1,
            Balance = 500m, StartDate = DateTime.UtcNow.Date.AddDays(3),
            ResponsibleUserId = "seller-1"
        });
        await ctx.SaveChangesAsync();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableUpcomingUnpaidReservationNotifications = true,
                UpcomingUnpaidReservationAlertDays = 7
            });

        var monitor = new OperationalFinanceMonitorService(
            ctx, settingsMock.Object, NewNotificationService(ctx), BuildUserManagerWithNoAdmins().Object);

        // 1ra corrida: crea el aviso.
        await monitor.GenerateUpcomingUnpaidReservationNotificationsAsync();
        Assert.Equal(1, await ctx.Notifications.CountAsync());

        // 2da corrida (mismo "día siguiente" simulado): NO duplica porque ya hay uno vivo con la misma clave.
        await monitor.GenerateUpcomingUnpaidReservationNotificationsAsync();
        Assert.Equal(1, await ctx.Notifications.CountAsync());

        // El dueño lo marca leído (o el sistema lo resolvió) → deja de estar vivo, pero la deuda sigue.
        var existing = await ctx.Notifications.FirstAsync();
        existing.ResolvedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        // 3ra corrida: la causa persiste y ya no hay vivo → recordatorio legítimo (uno nuevo).
        await monitor.GenerateUpcomingUnpaidReservationNotificationsAsync();
        Assert.Equal(2, await ctx.Notifications.CountAsync());
        Assert.Equal(1, await ctx.Notifications.CountAsync(n => n.ResolvedAt == null));
    }
}
