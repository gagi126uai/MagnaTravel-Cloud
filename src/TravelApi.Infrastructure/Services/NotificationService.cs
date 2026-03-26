using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;

using Microsoft.AspNetCore.SignalR;
using TravelApi.Hubs;

namespace TravelApi.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly IRepository<Notification> _notificationRepo;
    private readonly IHubContext<NotificationHub> _hubContext;

    public NotificationService(IRepository<Notification> notificationRepo, IHubContext<NotificationHub> hubContext)
    {
        _notificationRepo = notificationRepo;
        _hubContext = hubContext;
    }

    public async Task<IEnumerable<Notification>> GetUnreadNotificationsAsync(string userId, CancellationToken ct)
    {
        return await _notificationRepo.Query()
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(int id, string userId, CancellationToken ct)
    {
        var notification = await _notificationRepo.GetByIdAsync(id, ct);
        if (notification == null || notification.UserId != userId) return false;

        notification.IsRead = true;
        await _notificationRepo.UpdateAsync(notification, ct);
        return true;
    }

    public async Task<Notification> CreateAndSendAsync(Notification notification, CancellationToken ct = default)
    {
        notification.CreatedAt = DateTime.UtcNow;
        notification.IsRead = false;
        
        await _notificationRepo.AddAsync(notification, ct);

        // Emitir a SignalR al cliente específico por UserId
        await _hubContext.Clients.User(notification.UserId).SendAsync("ReceiveNotification", notification, cancellationToken: ct);

        return notification;
    }
}
