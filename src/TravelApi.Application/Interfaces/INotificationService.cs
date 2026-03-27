using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface INotificationService
{
    Task<IEnumerable<Notification>> GetUnreadNotificationsAsync(string userId, CancellationToken ct);
    Task<IEnumerable<Notification>> GetUrgentNotificationsAsync(string userId, CancellationToken ct);
    Task<bool> MarkAsReadAsync(int id, string userId, CancellationToken ct);
    Task<bool> DismissAsync(int id, string userId, CancellationToken ct);
    Task<Notification> CreateAndSendAsync(Notification notification, CancellationToken ct = default);
}
