using Hangfire;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// (2026-07-04) Barrido PROPIO que cierra las anulaciones trabadas "esperando reembolso" cuando el operador no
/// tiene nada que devolver (receivable $0 y nunca hubo circuito de reembolso). La logica de dominio vive en
/// <see cref="IBookingCancellationService.CloseZeroReceivableCancellationsAsync"/>; este job es solo el envoltorio
/// de Hangfire (mismo patron fino que <see cref="OperatorRefundTimeoutJob"/>).
///
/// <para><b>Por que un job PROPIO</b>: hasta ahora este barrido corria SOLO como cola de
/// <see cref="IBookingCancellationService.ProcessExpiredOperatorRefundsAsync"/> (el job de timeouts de las 4am).
/// Si la query inicial de vencidas de ese job explotaba (fila inconsistente, timeout de base), la excepcion cortaba
/// la corrida ANTES de llegar al barrido y esa noche NINGUNA anulacion sin receivable se cerraba. Registrarlo como
/// recurring job independiente lo desacopla: aunque el de timeouts falle, este igual barre. Es la red de seguridad.</para>
///
/// <para><b>Relacion con el job de timeouts</b>: el barrido SIGUE invocandose ademas al final de
/// <c>ProcessExpiredOperatorRefundsAsync</c> (4am) a proposito — asi una Awaiting recien-abandonada por vencimiento
/// se cierra en la MISMA corrida sin esperar a la noche siguiente. Este job corre a las 5am (una hora despues) para
/// no solaparse con aquel. Como <c>CloseZeroReceivableCancellationsAsync</c> es idempotente (una ya <c>Closed</c>
/// deja de ser candidata) y aisla la fila veneno (cada cierre en su propio SaveChanges con re-chequeo bajo la
/// instancia trackeada), correr las dos veces NO reprocesa ni duplica nada.</para>
///
/// <para><b>Concurrencia</b>: <c>[DisableConcurrentExecution]</c> es el guard de Hangfire contra dos corridas de
/// ESTE job (programada + reintento) solapadas. El guard de Hangfire es por-job-id, asi que no serializa contra el
/// job de timeouts; pero como el metodo de dominio es idempotente y re-chequea el estado bajo la fila trackeada
/// antes de cerrar, una superposicion entre ambos jobs es segura a nivel datos (el segundo ve la BC ya cerrada y es
/// no-op).</para>
/// </summary>
public class ZeroReceivableCancellationCloseJob
{
    private readonly IBookingCancellationService _bookingCancellationService;
    private readonly ILogger<ZeroReceivableCancellationCloseJob> _logger;

    public ZeroReceivableCancellationCloseJob(
        IBookingCancellationService bookingCancellationService,
        ILogger<ZeroReceivableCancellationCloseJob> logger)
    {
        _bookingCancellationService = bookingCancellationService;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta una pasada. Hangfire la invoca con la cron registrada en Program.cs; tambien es invocable a mano
    /// (admin/tests). Delega TODO el trabajo (busqueda + cierre + auditoria) en el service de dominio.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        var closed = await _bookingCancellationService.CloseZeroReceivableCancellationsAsync(ct);

        // El detalle por cancelacion ya lo loguea el service. Aca solo el resumen de la corrida.
        if (closed > 0)
        {
            _logger.LogWarning(
                "ZeroReceivableCancellationCloseJob: {Closed} anulacion(es) cerradas por no tener reembolso pendiente del operador.",
                closed);
        }
        else
        {
            _logger.LogDebug("ZeroReceivableCancellationCloseJob: ninguna anulacion trabada sin reembolso pendiente.");
        }
    }
}
