using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño): endpoint LIVIANO y ANÓNIMO que el front sondea
/// mientras muestra la pantalla especial "estamos restaurando el sistema, volvemos en un minuto".
///
/// <para><b>Por qué anónimo</b>: durante el mantenimiento, TODO <c>/api/**</c> devuelve 503 (ver
/// <see cref="TravelApi.Middleware.MaintenanceModeMiddleware"/>) — ni siquiera el login funcionaría, así que
/// este endpoint no puede exigir sesión. No expone nada sensible (ver <see cref="SystemStatusResponse"/>).</para>
/// </summary>
[ApiController]
[Route("api/system/status")]
[AllowAnonymous]
public class SystemStatusController : ControllerBase
{
    private readonly IMaintenanceModeService _maintenanceMode;

    public SystemStatusController(IMaintenanceModeService maintenanceMode)
    {
        _maintenanceMode = maintenanceMode;
    }

    [HttpGet]
    public ActionResult<SystemStatusResponse> Get()
    {
        return Ok(new SystemStatusResponse
        {
            EnMantenimiento = _maintenanceMode.IsActive,
            Motivo = _maintenanceMode.Reason,
            Desde = _maintenanceMode.SinceUtc,
        });
    }
}
