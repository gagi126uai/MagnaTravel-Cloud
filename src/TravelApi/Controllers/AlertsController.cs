using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
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

    /// <summary>
    /// ADR-019 D4: boton "Listo" del aviso "Proximos inicios" — descarte GLOBAL por reserva.
    /// Idempotente (repetir el POST pisa la misma fila). 204 siempre que la reserva exista y el
    /// flag este prendido; 404 si el flag esta apagado o la reserva no existe (ese 404 solo lo ven
    /// Admin/reservas.view_all: un vendedor comun recibe 403 del filtro ANTES, porque el ownership
    /// devuelve false tanto para reserva ajena como inexistente — sin leak de existencia).
    /// </summary>
    [HttpPost("upcoming-starts/{reservaPublicId}/dismiss")]
    [RequireOwnership(OwnedEntity.Reserva, routeParam: "reservaPublicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> DismissUpcomingStart(string reservaPublicId, CancellationToken cancellationToken)
    {
        // El filtro de ownership ya corto a 401 los requests sin identidad, asi que aca el claim existe.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var outcome = await _alertService.DismissUpcomingStartAsync(reservaPublicId, userId, cancellationToken);

        return outcome switch
        {
            // Flag OFF: la feature "no existe" como recurso.
            UpcomingStartDismissOutcome.FeatureDisabled => NotFound(),
            UpcomingStartDismissOutcome.ReservaNotFound => NotFound(),
            // Dismissed y NoUpcomingStart (no-op sin servicios) son exito idempotente.
            _ => NoContent()
        };
    }
}
