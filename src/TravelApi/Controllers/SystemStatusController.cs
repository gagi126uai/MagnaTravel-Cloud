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
        // Se lee UNA vez: el servicio releé el archivo cada 2 segundos, y leer la propiedad tres veces podría
        // devolver un paso de una lectura y el resto de otra (una foto incoherente por nada).
        var currentStep = _maintenanceMode.CurrentStep;

        return Ok(new SystemStatusResponse
        {
            EnMantenimiento = _maintenanceMode.IsActive,
            Motivo = _maintenanceMode.Reason,
            Desde = _maintenanceMode.SinceUtc,
            // Rediseño 2026-07-30 (§7 punto 2): si el código no es uno de los tres conocidos, TextFor devuelve
            // null y no se manda el paso — un valor raro nunca llega a la pantalla como texto crudo (T-5).
            Paso = RestoreProgressSteps.TextFor(currentStep) is null ? null : currentStep,
            PasoTexto = RestoreProgressSteps.TextFor(currentStep),
        });
    }
}
