using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Controllers;

/// <summary>
/// Endpoints de mantenimiento manual del sistema, restringidos a Admin.
/// Permiten correr on-demand jobs que normalmente corren via Hangfire (ej.
/// el lifecycle de reservas) sin esperar al schedule diario.
/// </summary>
[ApiController]
[Route("api/admin/maintenance")]
[Authorize(Roles = "Admin")]
public class AdminMaintenanceController : ControllerBase
{
    private readonly ReservaLifecycleAutomationService _lifecycle;
    private readonly ILogger<AdminMaintenanceController> _logger;

    public AdminMaintenanceController(
        ReservaLifecycleAutomationService lifecycle,
        ILogger<AdminMaintenanceController> logger)
    {
        _lifecycle = lifecycle;
        _logger = logger;
    }

    /// <summary>
    /// Corre el lifecycle de reservas inmediatamente (no espera al cron de las 3am UTC):
    /// promueve Reservado -> Operativo cuando arranca el viaje o se cobro todo, y cierra
    /// Operativo -> Cerrado para reservas cuyo EndDate ya paso. Devuelve los counts.
    /// </summary>
    [HttpPost("lifecycle-run")]
    public async Task<ActionResult<LifecycleRunResult>> RunLifecycle(CancellationToken ct)
    {
        try
        {
            var actor = User?.Identity?.Name ?? "unknown";
            _logger.LogInformation("Lifecycle automation triggered manually by {User}", actor);
            var result = await _lifecycle.RunDailyDetailedAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lifecycle manual run failed");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo ejecutar el lifecycle.",
                detail: ex.Message);
        }
    }
}
