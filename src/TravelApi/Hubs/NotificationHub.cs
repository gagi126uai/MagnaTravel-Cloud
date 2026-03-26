using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TravelApi.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    // Este hub permite enviar notificaciones push a los clientes conectados.
    // El frontend se conectará acá para escuchar el evento 'ReceiveNotification'.
    
    public override async Task OnConnectedAsync()
    {
        // Los usuarios ya están mapeados por su NameIdentifier (UserId) gracias al JWT automatically
        await base.OnConnectedAsync();
    }
}
