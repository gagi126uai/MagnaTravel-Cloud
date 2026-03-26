using Microsoft.AspNetCore.SignalR;

namespace TravelApi.Hubs;

/// <summary>
/// Marker hub class used by IHubContext&lt;NotificationHub&gt; for DI.
/// The actual hub registration is in the API project.
/// </summary>
public class NotificationHub : Hub
{
}
