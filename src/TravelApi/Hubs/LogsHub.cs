using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace TravelApi.Hubs
{
    [Authorize(Roles = "Admin")]
    public class LogsHub : Hub
    {
        public const string AdminGroup = "admins";

        public override async Task OnConnectedAsync()
        {
            if (!Context.User?.IsInRole("Admin") ?? true)
            {
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
            await base.OnConnectedAsync();
        }
    }
}
