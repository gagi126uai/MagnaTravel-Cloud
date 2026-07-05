using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Interfaces;
using TravelApi.Contracts;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUnread(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var notifications = await _notificationService.GetUnreadNotificationsAsync(userId, ct);
        return Ok(notifications.Select(NotificationDto.FromEntity));
    }

    [HttpGet("urgent")]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUrgent(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var notifications = await _notificationService.GetUrgentNotificationsAsync(userId, ct);
        return Ok(notifications.Select(NotificationDto.FromEntity));
    }

    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkAsRead(int id, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var success = await _notificationService.MarkAsReadAsync(id, userId, ct);
        if (!success) return NotFound();

        return NoContent();
    }

    [HttpPost("{id}/dismiss")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var success = await _notificationService.DismissAsync(id, userId, ct);
        if (!success) return NotFound();

        return NoContent();
    }
}
