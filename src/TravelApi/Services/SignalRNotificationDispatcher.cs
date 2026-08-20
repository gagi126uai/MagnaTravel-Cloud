using Microsoft.AspNetCore.SignalR;
using TravelApi.Application.Interfaces;
using TravelApi.Contracts;
using TravelApi.Domain.Entities;
using TravelApi.Hubs;

namespace TravelApi.Services;

public class SignalRNotificationDispatcher : INotificationRealtimeDispatcher
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly INotificationTargetUrlResolver _targetUrlResolver;

    public SignalRNotificationDispatcher(IHubContext<NotificationHub> hubContext, INotificationTargetUrlResolver targetUrlResolver)
    {
        _hubContext = hubContext;
        _targetUrlResolver = targetUrlResolver;
    }

    public async Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        // (Tanda 5, 2026-07-05 — data-exposure gate) Enrutamos por UserId (server-side) pero el PAYLOAD que viaja al
        // navegador es el DTO proyectado, no la entidad EF: así el push en tiempo real no filtra campos internos
        // (UserId, ResolutionKey "Invoice:42", ResolvedAt, RelatedEntityType/Id) igual que el GET REST.
        // TargetUrl (2026-08-19): un item -> ResolveManyAsync con lista de 1, mismo resolver que el GET REST.
        var targetUrls = await _targetUrlResolver.ResolveManyAsync(new[] { notification }, cancellationToken);
        var payload = NotificationDto.FromEntity(notification, targetUrls.GetValueOrDefault(notification.Id));

        await _hubContext.Clients.User(notification.UserId)
            .SendAsync("ReceiveNotification", payload, cancellationToken: cancellationToken);

        if (notification.Priority == "Urgent")
        {
            await _hubContext.Clients.User(notification.UserId)
                .SendAsync("ReceiveUrgentBanner", payload, cancellationToken: cancellationToken);
        }
    }
}
