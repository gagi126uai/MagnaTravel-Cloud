using Hangfire;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FIX B (2026-07-04): red de seguridad para el aviso de AFIP perdido en la NC TOTAL. Envoltorio fino de Hangfire
/// del metodo de dominio <see cref="IBookingCancellationService.ReconcileStuckFiscalConfirmationsAsync"/> (mismo
/// patron fino que <see cref="ZeroReceivableCancellationCloseJob"/> y <see cref="OperatorRefundTimeoutJob"/>).
///
/// <para><b>Por que existe</b>: la transicion <c>AwaitingFiscalConfirmation</c> -&gt; <c>AwaitingOperatorRefund</c>
/// depende 100% del callback de Hangfire que <c>InvoiceService.ProcessAnnulmentJob</c> dispara al terminar AFIP
/// (<c>OnArcaSucceededAsync</c>/<c>OnArcaFailedAsync</c>). Si la NC obtiene resultado final (CAE aprobado o rechazo)
/// pero ese callback muere de forma permanente, la cancelacion queda trabada en <c>AwaitingFiscalConfirmation</c> y
/// la reserva en <c>PendingOperatorRefund</c>, INVISIBLE a todos los barridos (filtran Awaiting/Abandoned) y a las
/// alertas. Este job es el equivalente TOTAL del <see cref="PartialCreditNoteBridgeReconciliationJob"/> que ya
/// existe para la NC parcial: detecta esos huerfanos y re-aplica el MISMO callback (idempotente).</para>
///
/// <para><b>Que NO hace</b>: no fuerza transiciones sin pasar por el callback del bridge (ese sigue siendo la unica
/// via para mutar el BC), no emite NC ni toca AFIP (la NC ya tiene su resultado; solo re-aplica el efecto que se
/// perdio). Si al reconciliar la BC queda sin plata pendiente del operador, el propio callback la auto-cierra —
/// eso es correcto y deseado (misma logica que el flujo normal post-CAE).</para>
///
/// <para><b>Concurrencia</b>: <c>[DisableConcurrentExecution]</c> evita que dos corridas de ESTE job (programada +
/// reintento) se solapen. El metodo de dominio es idempotente (una BC ya destrabada deja de ser candidata) y aisla
/// la fila veneno (cada BC en su propio intento), asi que una superposicion es segura a nivel datos.</para>
/// </summary>
public class TotalCreditNoteBridgeReconciliationJob
{
    private readonly IBookingCancellationService _bookingCancellationService;
    private readonly ILogger<TotalCreditNoteBridgeReconciliationJob> _logger;

    public TotalCreditNoteBridgeReconciliationJob(
        IBookingCancellationService bookingCancellationService,
        ILogger<TotalCreditNoteBridgeReconciliationJob> logger)
    {
        _bookingCancellationService = bookingCancellationService;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta una pasada. Hangfire la invoca con la cron registrada en Program.cs; tambien es invocable a mano
    /// (admin/tests). Delega TODO el trabajo (busqueda + re-aplicacion del callback + auditoria) en el dominio.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        var reconciled = await _bookingCancellationService.ReconcileStuckFiscalConfirmationsAsync(ct);

        if (reconciled > 0)
        {
            _logger.LogWarning(
                "TotalCreditNoteBridgeReconciliationJob: {Reconciled} anulacion(es) destrabadas (AFIP ya habia respondido pero el aviso se perdio).",
                reconciled);
        }
        else
        {
            _logger.LogDebug("TotalCreditNoteBridgeReconciliationJob: ninguna anulacion trabada esperando confirmacion fiscal.");
        }
    }
}
