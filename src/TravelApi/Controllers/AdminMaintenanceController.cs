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
    private readonly CoherenceMoneyRecalculator _coherenceMoneyRecalculator;
    private readonly CoherenceWatchdogJob _coherenceWatchdogJob;
    private readonly ILogger<AdminMaintenanceController> _logger;

    public AdminMaintenanceController(
        ReservaLifecycleAutomationService lifecycle,
        CoherenceMoneyRecalculator coherenceMoneyRecalculator,
        CoherenceWatchdogJob coherenceWatchdogJob,
        ILogger<AdminMaintenanceController> logger)
    {
        _lifecycle = lifecycle;
        _coherenceMoneyRecalculator = coherenceMoneyRecalculator;
        _coherenceWatchdogJob = coherenceWatchdogJob;
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
            // No se filtra el detalle técnico al usuario: el mensaje es de negocio y el error real queda en el log.
            _logger.LogError(ex, "Lifecycle manual run failed");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo actualizar el estado de las reservas.",
                detail: "Ocurrió un problema al procesar. Volvé a intentarlo; si persiste, avisá al equipo.");
        }
    }

    /// <summary>
    /// (2026-07-04, hallazgo A1) Recalcula la plata de las reservas ANULADAS cuya cuenta quedó desactualizada, para
    /// que dejen de mostrar deuda fantasma. Es el PASO 2 de la reparación: la migración
    /// <c>RepairLegacyAnnulledReservaServices</c> ya canceló en la base los servicios que habían quedado vivos; este
    /// endpoint corre los recálculos de dominio (los mismos que una anulación moderna) para bajar la venta confirmada
    /// y el saldo a lo real.
    ///
    /// <para>Idempotente: correrlo de nuevo sobre reservas ya sanas no cambia nada (0 corregidas). Devuelve un
    /// resumen en lenguaje de negocio; ante un error interno responde un mensaje amable, sin detalle técnico.</para>
    /// </summary>
    [HttpPost("coherence/recalculate-money")]
    public async Task<ActionResult<CoherenceRecalculationResponse>> RecalculateAnnulledReservasMoney(CancellationToken ct)
    {
        try
        {
            var actor = User?.Identity?.Name ?? "unknown";
            _logger.LogInformation("Coherence money recalculation triggered manually by {User}", actor);

            var result = await _coherenceMoneyRecalculator.RecalculateAnnulledReservasMoneyAsync(ct);

            var summary = BuildRecalculationSummary(result);
            return Ok(new CoherenceRecalculationResponse(
                ReservasRevisadas: result.Reviewed,
                ReservasCorregidas: result.Corrected,
                ReservasConError: result.Failed,
                Resumen: summary));
        }
        catch (Exception ex)
        {
            // No se filtra el detalle técnico al usuario: el mensaje es de negocio y el error real queda en el log.
            _logger.LogError(ex, "Coherence money recalculation failed");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo recalcular la coherencia de la plata.",
                detail: "Ocurrió un problema al recalcular. Volvé a intentarlo; si persiste, avisá al equipo.");
        }
    }

    /// <summary>
    /// (Tanda 4, 2026-07-04) Corre el vigía de coherencia inmediatamente (no espera al cron de las 6am UTC): repara
    /// lo seguro (marcas de revisión colgadas, cuentas de plata desactualizadas) y reporta el resto (anuladas con
    /// servicios sin cancelar o con deuda sin comprobante). Devuelve un resumen en lenguaje de negocio.
    ///
    /// <para>Idempotente: correrlo de nuevo sobre datos ya sanos no cambia nada ni vuelve a notificar. Ante un error
    /// interno responde un mensaje amable, sin detalle técnico.</para>
    /// </summary>
    [HttpPost("coherence/run-watchdog")]
    public async Task<ActionResult<CoherenceWatchdogResponse>> RunCoherenceWatchdog(CancellationToken ct)
    {
        try
        {
            var actor = User?.Identity?.Name ?? "unknown";
            _logger.LogInformation("Coherence watchdog triggered manually by {User}", actor);

            var result = await _coherenceWatchdogJob.RunAsync(ct);

            var repaired = result.AutoRepairedMarks + result.AutoRepairedMoney + result.AnnulledMoneyRecalculated;
            var toReview = result.AnnulledWithLiveServices + result.AnnulledWithUnjustifiedDebt;

            return Ok(new CoherenceWatchdogResponse(
                Reparadas: repaired,
                ParaRevisar: toReview,
                SeAvisoAlEquipo: result.NotificationSent,
                Resumen: BuildWatchdogSummary(repaired, toReview)));
        }
        catch (CoherenceWatchdogBusyException)
        {
            // Ya hay una corrida en curso (esta manual o la programada de la noche). No es un error: se le avisa al
            // usuario que espere, sin ningún detalle técnico. 409 Conflict = "el recurso está ocupado ahora".
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Ya hay un chequeo corriendo.",
                detail: "Ya hay un chequeo corriendo, esperá a que termine.");
        }
        catch (Exception ex)
        {
            // No se filtra el detalle técnico al usuario: el mensaje es de negocio y el error real queda en el log.
            _logger.LogError(ex, "Coherence watchdog manual run failed");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo correr el chequeo nocturno de datos.",
                detail: "Ocurrió un problema al revisar. Volvé a intentarlo; si persiste, avisá al equipo.");
        }
    }

    /// <summary>Arma el resumen en castellano del vigía, según cuánto se reparó solo y cuánto quedó para revisar.</summary>
    private static string BuildWatchdogSummary(int repaired, int toReview)
    {
        if (repaired == 0 && toReview == 0)
            return "No se encontró nada raro en los datos. Todo en orden.";

        string repairedPhrase;
        if (repaired == 0)
            repairedPhrase = "No hubo nada para corregir automáticamente.";
        else if (repaired == 1)
            repairedPhrase = "Se corrigió sola 1 cosa que no cuadraba.";
        else
            repairedPhrase = $"Se corrigieron solas {repaired} cosas que no cuadraban.";

        string reviewPhrase;
        if (toReview == 0)
            reviewPhrase = "No quedó nada para revisar.";
        else if (toReview == 1)
            reviewPhrase = "Quedó 1 reserva anulada para que revises.";
        else
            reviewPhrase = $"Quedaron {toReview} reservas anuladas para que revises.";

        return repairedPhrase + " " + reviewPhrase;
    }

    /// <summary>Arma el resumen en castellano que ve el usuario, según cuántas reservas se revisaron/corrigieron.</summary>
    private static string BuildRecalculationSummary(
        CoherenceMoneyRecalculator.CoherenceRecalculationResult result)
    {
        if (result.Reviewed == 0)
            return "No había reservas anuladas con la cuenta desactualizada. Todo en orden.";

        var summary = $"Se revisaron {result.Reviewed} reservas anuladas y se corrigieron {result.Corrected}.";
        if (result.Failed > 0)
            summary += $" Quedaron {result.Failed} sin poder recalcular; se pueden reintentar.";
        return summary;
    }
}

/// <summary>
/// Respuesta del recálculo de coherencia de plata: números y un resumen en lenguaje de negocio. NO expone nada
/// técnico (ids, nombres internos, estados crudos): solo lo que un usuario no programador necesita entender.
/// </summary>
public sealed record CoherenceRecalculationResponse(
    int ReservasRevisadas,
    int ReservasCorregidas,
    int ReservasConError,
    string Resumen);

/// <summary>
/// Respuesta de la corrida manual del vigía de coherencia: cuánto se reparó solo, cuánto quedó para revisar, si se
/// avisó al equipo y un resumen en lenguaje de negocio. NO expone nada técnico (ids, códigos internos, estados crudos).
/// </summary>
public sealed record CoherenceWatchdogResponse(
    int Reparadas,
    int ParaRevisar,
    bool SeAvisoAlEquipo,
    string Resumen);
