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
    private readonly INotificationTargetUrlResolver _targetUrlResolver;

    public NotificationsController(INotificationService notificationService, INotificationTargetUrlResolver targetUrlResolver)
    {
        _notificationService = notificationService;
        _targetUrlResolver = targetUrlResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUnread(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var notifications = (await _notificationService.GetUnreadNotificationsAsync(userId, ct)).ToList();
        var targetUrls = await _targetUrlResolver.ResolveManyAsync(notifications, ct);
        return Ok(notifications.Select(n => NotificationDto.FromEntity(n, targetUrls.GetValueOrDefault(n.Id))));
    }

    [HttpGet("urgent")]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUrgent(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var notifications = (await _notificationService.GetUrgentNotificationsAsync(userId, ct)).ToList();
        var targetUrls = await _targetUrlResolver.ResolveManyAsync(notifications, ct);
        return Ok(notifications.Select(n => NotificationDto.FromEntity(n, targetUrls.GetValueOrDefault(n.Id))));
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
