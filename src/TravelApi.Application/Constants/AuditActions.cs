namespace TravelApi.Application.Constants;

/// <summary>
/// FC1.2.1 v3 (2026-05-17): catalogo de strings de <c>action</c> que se pasan
/// a <c>IAuditService.LogBusinessEventAsync(action, ...)</c>.
///
/// <para>
/// <b>Por que constants en vez de string literals</b>: si un dia renombrabamos
/// "BookingCancellationConfirmed" en el service pero olvidabamos hacerlo en
/// los tests / dashboards, los reports de auditoria quedaban desincronizados
/// silenciosamente. Con constants compartidas, un rename rompe el build de
/// todos los callers — el cambio se detecta inmediato.
/// </para>
///
/// <para>
/// <b>Convencion de naming</b>: PascalCase, evento en pasado ("...Confirmed",
/// "...Aborted"). No agregar el prefijo "Audit" ni el sufijo "Event" — el caller
/// ya sabe que es un audit log. Si el evento tiene variantes manuales vs
/// automaticas (ej. Arca Confirmation), discriminar con sufijo claro
/// (..."Manually") para que las queries de auditoria puedan distinguirlas.
/// </para>
/// </summary>
public static class AuditActions
{
    // ===== Modulo cancelacion/refund (FC1.2) =====

    /// <summary>
    /// FC1.2.1: BC creado en estado <c>Drafted</c> (T-1). EntityName=BookingCancellation,
    /// EntityId = BC.Id (legacy int).
    /// </summary>
    public const string BookingCancellationDrafted = "BookingCancellationDrafted";

    /// <summary>
    /// FC1.2.1: BC pasa a <c>AwaitingFiscalConfirmation</c> (T0). El detalle JSON
    /// incluye <c>approvalRequestPublicId</c> cuando hubo override admin.
    /// </summary>
    public const string BookingCancellationConfirmed = "BookingCancellationConfirmed";

    /// <summary>
    /// FC1.2.1: BC abortado desde <c>Drafted</c> (sin side-effects fiscales).
    /// </summary>
    public const string BookingCancellationAborted = "BookingCancellationAborted";

    /// <summary>
    /// FC1.2.1: AFIP devolvio CAE para la NC; el BC paso a
    /// <c>AwaitingOperatorRefund</c> automaticamente via callback Hangfire.
    /// </summary>
    public const string BookingCancellationArcaSucceeded = "BookingCancellationArcaSucceeded";

    /// <summary>
    /// FC1.2.1: AFIP rechazo la NC; el BC paso a <c>ArcaRejected</c>.
    /// El detalle JSON incluye el <c>afipErrorMessage</c>.
    /// </summary>
    public const string BookingCancellationArcaRejected = "BookingCancellationArcaRejected";

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): Admin forzo la transicion fiscal usando el escape
    /// hatch <c>ForceArcaConfirmationAsync</c>. Discrimina del flujo automatico
    /// para queries de auditoria ("cuantas BCs se confirmaron por callback vs
    /// por boton manual?").
    /// </summary>
    public const string BookingCancellationArcaConfirmedManually = "BookingCancellationArcaConfirmedManually";

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): variante no-op del Force cuando el BC ya
    /// transiciono via callback automatico antes de que el Admin apretara el
    /// boton. La operacion es idempotente (200 OK), pero el audit log distinto
    /// permite trazar el intento.
    /// </summary>
    public const string BookingCancellationArcaConfirmedManuallyNoOp = "BookingCancellationArcaConfirmedManually_NoOp";

    // ===== Entity name (helper) =====

    /// <summary>
    /// Nombre canonico de entidad que se pasa a <c>LogBusinessEventAsync(entityName: ...)</c>
    /// para todos los eventos del modulo. Si el frontend filtra audit logs por
    /// <c>entityName=BookingCancellation</c>, este es el valor a usar.
    /// </summary>
    public const string BookingCancellationEntityName = "BookingCancellation";
}
