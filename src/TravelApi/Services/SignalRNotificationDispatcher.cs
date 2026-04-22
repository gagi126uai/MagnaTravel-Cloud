using Microsoft.AspNetCore.SignalR;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Hubs;

namespace TravelApi.Services;

public class SignalRNotificationDispatcher : INotificationRealtimeDispatcher
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public SignalRNotificationDispatcher(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.User(notification.UserId)
            .SendAsync("ReceiveNotification", notification, cancellationToken: cancellationToken);

        if (notification.Priority == "Urgent")
        {
            await _hubContext.Clients.User(notification.UserId)
                .SendAsync("ReceiveUrgentBanner", notification, cancellationToken: cancellationToken);
        }
    }
}
