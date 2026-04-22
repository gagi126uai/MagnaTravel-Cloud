using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly IRepository<Notification> _notificationRepo;
    private readonly INotificationRealtimeDispatcher _dispatcher;

    public NotificationService(
        IRepository<Notification> notificationRepo,
        INotificationRealtimeDispatcher dispatcher)
    {
        _notificationRepo = notificationRepo;
        _dispatcher = dispatcher;
    }

    public async Task<IEnumerable<Notification>> GetUnreadNotificationsAsync(string userId, CancellationToken ct)
    {
        return await _notificationRepo.Query()
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IEnumerable<Notification>> GetUrgentNotificationsAsync(string userId, CancellationToken ct)
    {
        return await _notificationRepo.Query()
            .Where(n => n.UserId == userId && n.Priority == "Urgent" && !n.IsDismissed)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(int id, string userId, CancellationToken ct)
    {
        var notification = await _notificationRepo.GetByIdAsync(id, ct);
        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        notification.IsRead = true;
        await _notificationRepo.UpdateAsync(notification, ct);
        return true;
    }

    public async Task<bool> DismissAsync(int id, string userId, CancellationToken ct)
    {
        var notification = await _notificationRepo.GetByIdAsync(id, ct);
        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        notification.IsDismissed = true;
        await _notificationRepo.UpdateAsync(notification, ct);
        return true;
    }

    public async Task<Notification> CreateAndSendAsync(Notification notification, CancellationToken ct = default)
    {
        notification.CreatedAt = DateTime.UtcNow;
        notification.IsRead = false;
        notification.IsDismissed = false;

        await _notificationRepo.AddAsync(notification, ct);
        await _dispatcher.DispatchAsync(notification, ct);

        return notification;
    }
}
