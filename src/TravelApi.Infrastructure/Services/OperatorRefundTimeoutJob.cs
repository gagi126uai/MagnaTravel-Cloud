using Hangfire;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// (2026-06-26) Job nocturno que cierra el ciclo del reembolso del operador: detecta las cancelaciones trabadas
/// en <c>AwaitingOperatorRefund</c> cuyo plazo (<c>OperatorRefundDueBy</c>) ya vencio y las transiciona a
/// <c>AbandonedByOperator</c> (cerrando la reserva). La logica de dominio vive en
/// <see cref="IBookingCancellationService.ProcessExpiredOperatorRefundsAsync"/>; este job es solo el envoltorio
/// de Hangfire (mismo patron fino que <see cref="PartialCreditNoteReviewAlertJob"/>).
///
/// <para><b>Por que existe</b>: antes de este fix el estado <c>AbandonedByOperator</c> nunca se asignaba (codigo
/// muerto) y no habia job que mirara <c>OperatorRefundDueBy</c>. Cuando el operador no devolvia el reembolso, la
/// cuenta por cobrar quedaba colgada para siempre sin alerta. Este job cierra ese hueco; ademas
/// <c>AlertService</c> expone las vencidas/abandonadas para que el usuario las vea.</para>
///
/// <para><b>Concurrencia</b>: <c>[DisableConcurrentExecution]</c> es el guard de Hangfire contra dos corridas
/// (programada + reintento) solapadas — la segunda espera al lock distribuido de Postgres en vez de correr en
/// paralelo. El metodo de dominio ademas es idempotente y aisla la fila veneno, asi que una superposicion seria
/// segura a nivel datos; el guard evita el doble trabajo.</para>
/// </summary>
public class OperatorRefundTimeoutJob
{
    private readonly IBookingCancellationService _bookingCancellationService;
    private readonly ILogger<OperatorRefundTimeoutJob> _logger;

    public OperatorRefundTimeoutJob(
        IBookingCancellationService bookingCancellationService,
        ILogger<OperatorRefundTimeoutJob> logger)
    {
        _bookingCancellationService = bookingCancellationService;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta una pasada. Hangfire la invoca con la cron registrada en Program.cs; tambien es invocable a mano
    /// (admin/tests). Delega TODO el trabajo (busqueda + transicion + auditoria) en el service de dominio.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        var abandoned = await _bookingCancellationService.ProcessExpiredOperatorRefundsAsync(ct);

        // Log informativo de la corrida (el detalle por cancelacion ya lo loguea el service). Un valor > 0
        // sostenido es accionable: hay operadores que sistematicamente no reembolsan en plazo.
        if (abandoned > 0)
        {
            _logger.LogWarning(
                "OperatorRefundTimeoutJob: {Abandoned} cancelacion(es) marcadas AbandonedByOperator por plazo de reembolso vencido.",
                abandoned);
        }
        else
        {
            _logger.LogDebug("OperatorRefundTimeoutJob: ninguna cancelacion con reembolso del operador vencido.");
        }
    }
}
