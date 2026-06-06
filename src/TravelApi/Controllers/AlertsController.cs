using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AlertsController : ControllerBase
{
    private readonly IAlertService _alertService;
    private readonly IUserPermissionResolver _permissionResolver;

    public AlertsController(IAlertService alertService, IUserPermissionResolver permissionResolver)
    {
        _alertService = alertService;
        _permissionResolver = permissionResolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetAlerts(CancellationToken cancellationToken)
    {
        // Fuga 2 (ADR-017 F1b) + F1.4: el service decide que buckets ve cada caller. Aca solo se traduce
        // la identidad de los claims al contrato del service, incluido si puede ver costos (gatea el
        // bucket CostsToConfirm server-side, §2.8/D8b — mismo criterio que CostMasking).
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = User.IsInRole("Admin");

        var canSeeCost = isAdmin
            || (!string.IsNullOrEmpty(userId)
                && (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                    .Contains(Permissions.CobranzasSeeCost));

        var caller = new AlertCallerContext(UserId: userId, IsAdmin: isAdmin, CanSeeCost: canSeeCost);

        var alerts = await _alertService.GetAlertsAsync(caller, cancellationToken);
        return Ok(alerts);
    }
}
