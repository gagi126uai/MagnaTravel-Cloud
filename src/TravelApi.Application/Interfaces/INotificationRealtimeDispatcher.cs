using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface INotificationRealtimeDispatcher
{
    Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default);
}
