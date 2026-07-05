using Microsoft.AspNetCore.SignalR;
using TravelApi.Application.Interfaces;
using TravelApi.Contracts;
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
        // (Tanda 5, 2026-07-05 — data-exposure gate) Enrutamos por UserId (server-side) pero el PAYLOAD que viaja al
        // navegador es el DTO proyectado, no la entidad EF: así el push en tiempo real no filtra campos internos
        // (UserId, ResolutionKey "Invoice:42", ResolvedAt, RelatedEntityType/Id) igual que el GET REST.
        var payload = NotificationDto.FromEntity(notification);

        await _hubContext.Clients.User(notification.UserId)
            .SendAsync("ReceiveNotification", payload, cancellationToken: cancellationToken);

        if (notification.Priority == "Urgent")
        {
            await _hubContext.Clients.User(notification.UserId)
                .SendAsync("ReceiveUrgentBanner", payload, cancellationToken: cancellationToken);
        }
    }
}
