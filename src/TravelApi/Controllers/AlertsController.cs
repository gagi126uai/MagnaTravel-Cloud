using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AlertsController : ControllerBase
{
    private readonly IAlertService _alertService;

    public AlertsController(IAlertService alertService)
    {
        _alertService = alertService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAlerts(CancellationToken cancellationToken)
    {
        // Fuga 2 (ADR-017 F1b): el service decide que buckets ve cada caller.
        // Aca solo se traduce la identidad de los claims al contrato del service.
        var caller = new AlertCallerContext(
            UserId: User.FindFirstValue(ClaimTypes.NameIdentifier),
            IsAdmin: User.IsInRole("Admin"));

        var alerts = await _alertService.GetAlertsAsync(caller, cancellationToken);
        return Ok(alerts);
    }
}
