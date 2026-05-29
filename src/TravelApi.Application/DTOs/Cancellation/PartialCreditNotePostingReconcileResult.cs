namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3.F2.6a (rehecho 2026-05-28): resultado de intentar reconciliar UNA Nota de
/// Credito (NC) parcial que quedo colgada en <c>Resultado='PENDING'</c>.
///
/// <para><b>Por que existe</b>: el job recurrente
/// <c>PartialCreditNotePostingReconciliationJob</c> necesita saber QUE paso con cada NC
/// colgada para decidir si escala a revision manual (notifica admins) o si la NC se
/// resolvio sola. El metodo que hace el trabajo fiscal pesado vive en
/// <c>InvoiceService.ReconcileStuckPartialCreditNoteAsync</c> (reutiliza el stale-key
/// recovery del emisor); este enum le comunica al job el desenlace SIN que el job tenga
/// que volver a leer la base ni reinterpretar el estado fiscal.</para>
/// </summary>
public enum PartialCreditNotePostingReconcileOutcome
{
    /// <summary>
    /// ARCA confirmo que la NC SI se emitio en una corrida anterior (el proceso se cayo
    /// antes de persistir la respuesta). El recovery derivo el CAE real, marco la NC como
    /// aprobada, anulo la factura origen y sincronizo el BookingCancellation. El job NO
    /// tiene que hacer nada mas con esta NC.
    /// </summary>
    Confirmed,

    /// <summary>
    /// La key de idempotencia de esta NC todavia esta "en vuelo" (no vencida): el emisor
    /// original esta posteando AHORA, o un reintento de Hangfire esta corriendo. NO
    /// tocamos nada para no pisar al emisor (arregla M-1). El job espera el proximo ciclo.
    /// </summary>
    InFlight,

    /// <summary>
    /// ARCA NO confirma la NC (el POST nunca viajo) o la NC nunca llego a reservar numero
    /// (no tiene key). El recovery NO confirma a ciegas: re-encola la emision idempotente
    /// (el mismo job de emision, que vuelve a pasar por la idempotencia). El job NO escala
    /// a manual: la NC esta en camino de re-emitirse.
    /// </summary>
    ReEnqueuedEmission,

    /// <summary>
    /// No se pudo reconciliar de forma segura (falta un dato necesario para correlacionar
    /// la NC con su intento de emision: la factura origen no tiene approval vinculado, o la
    /// NC no tiene reserva). El job deja la NC para escalado a revision manual. NO re-emite
    /// ni confirma: necesita ojo humano.
    /// </summary>
    NeedsManualReview,
}

/// <summary>
/// FC1.3.F2.6a: resultado detallado de <c>ReconcileStuckPartialCreditNoteAsync</c>.
/// Inmutable (record posicional).
/// </summary>
/// <param name="Outcome">Desenlace de la reconciliacion (ver enum).</param>
/// <param name="Detail">
/// Texto corto para el log/notificacion (ej. el motivo del NeedsManualReview). Sin datos
/// sensibles (ni montos de pasajero ni CUIT): solo identificadores tecnicos.
/// </param>
public record PartialCreditNotePostingReconcileResult(
    PartialCreditNotePostingReconcileOutcome Outcome,
    string? Detail = null);
