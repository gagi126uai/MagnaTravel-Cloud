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
    /// Corre el lifecycle de reservas inmediatamente (no espera al cron de las 3am UTC): promueve Reservado -&gt;
    /// Operativo cuando arranca el viaje o se cobro todo, y cierra Operativo -&gt; Cerrado para reservas cuyo
    /// EndDate ya paso. Devuelve los counts (contrato SINCRONO que consume la solapa de Mantenimiento del front).
    ///
    /// <para>NOTA DE CONCURRENCIA (ARREGLO 2, 2026-06-25): la corrida PROGRAMADA (cron via Hangfire) esta
    /// protegida contra solapamiento por <c>[DisableConcurrentExecution]</c> en
    /// <see cref="ReservaLifecycleAutomationService.RunDailyAsync"/>. Esta corrida MANUAL es INLINE (no pasa por
    /// Hangfire) para mantener el contrato sincrono que el front necesita, asi que NO comparte ese lock. La
    /// proteccion interna del job (re-lectura de estado y de saldo antes de cada transicion, ARREGLO 1) hace que
    /// una eventual superposicion manual+programada sea segura a nivel datos: la segunda corrida simplemente ve
    /// los estados ya movidos y los saltea. El unico efecto de una superposicion seria trabajo repetido (no
    /// corrupcion). Si se quisiera cerrar tambien esa ventana, habria que encolar esta accion via Hangfire (lo
    /// que cambia el contrato a asincrono y requiere ajustar el front — gate UX pendiente).</para>
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
