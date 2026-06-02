namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// ADR-013 §3.10 (M4, 2026-06-01): fila de la bandeja operativa "cancelaciones con NC
/// emitida pero sin su ND". Existe para hacer OBSERVABLE el caso en que la NC total ya
/// salio con CAE pero la ND quedo pendiente o fallida -> la cancelacion esta fiscalmente
/// incompleta y alguien tiene que reintentar/revisar.
///
/// <para>Es un DTO de lectura plano (no expone la entidad de persistencia). Solo lleva lo
/// que la bandeja necesita mostrar; nada sensible del cliente/pasajero.</para>
/// </summary>
public class CancellationDebitNotePendingDto
{
    /// <summary>
    /// ADR-014 §3.8: pseudo-estado para el caso "penalidad confirmada (PenaltyStatus=Confirmed)
    /// pero NUNCA se creo la ND" (el motor rebanoto a ManualReview o no llego a crear nada).
    /// NO existe en el enum <c>DebitNoteStatus</c>: es un valor solo-de-lectura que la bandeja
    /// proyecta en texto para que el frontend lo distinga de Pending/Failed. Centralizado aca
    /// para que el servicio y los tests no dupliquen el literal.
    /// </summary>
    public const string ConfirmedWithoutDebitNotePseudoStatus = "ConfirmedWithoutDebitNote";

    /// <summary>
    /// ADR-014 (M-B2, 2026-06-02): pseudo-estado para el CASO DOMINANTE del negocio: la
    /// cancelacion tiene una penalidad de cargo PROPIO de la agencia que quedo en
    /// <c>PenaltyStatus=Estimated</c> (el operador todavia NO confirmo el monto), con la NC
    /// total ya emitida y SIN ND. Estas filas estan esperando que alguien vuelva a confirmar
    /// el monto definitivo (endpoint confirm-penalty) para recien ahi emitir la ND.
    ///
    /// <para>NO existe en el enum <c>DebitNoteStatus</c> (su valor real es
    /// <c>NotApplicable</c>, porque todavia no aplica una ND mientras la penalidad es estimada):
    /// es un valor solo-de-lectura que la bandeja proyecta para que el frontend distinga estas
    /// filas de las Pending/Failed y abra el ConfirmPenaltyModal en vez del reintento de ND.
    /// Centralizado aca para que el servicio y los tests no dupliquen el literal.</para>
    /// </summary>
    public const string EstimatedPendingConfirmationPseudoStatus = "EstimatedPendingConfirmation";

    /// <summary>PublicId del BookingCancellation (la UI navega por PublicId, nunca por Id int).</summary>
    public Guid BookingCancellationPublicId { get; set; }

    /// <summary>Numero de reserva (legible para el operador de la bandeja).</summary>
    public string ReservaNumero { get; set; } = string.Empty;

    /// <summary>
    /// Estado de la ND, en texto. Puede ser un valor del enum <c>DebitNoteStatus</c>
    /// (tipicamente "Pending" = encolada o "Failed" = fallo el CAE) O el pseudo-estado
    /// <see cref="ConfirmedWithoutDebitNotePseudoStatus"/> ("ConfirmedWithoutDebitNote",
    /// ADR-014 §3.8), que NO pertenece al enum: indica que la penalidad quedo confirmada
    /// pero la ND nunca llego a crearse. El frontend debe contemplar ambos casos.
    /// </summary>
    public string DebitNoteStatus { get; set; } = string.Empty;

    /// <summary>Monto de la penalidad congelado al momento del evento.</summary>
    public decimal? PenaltyAmount { get; set; }

    /// <summary>Moneda de la penalidad (ARS en el MVP).</summary>
    public string? PenaltyCurrency { get; set; }

    /// <summary>Tipo de comprobante de la ND (12 = ND C en el MVP).</summary>
    public int? DebitNoteCbteTipo { get; set; }

    /// <summary>Mensaje de error de ARCA si la ND fallo (motivo para el operador).</summary>
    public string? ArcaErrorMessage { get; set; }

    /// <summary>Momento en que se confirmo la cancelacion (para ordenar/priorizar).</summary>
    public DateTime? ConfirmedAt { get; set; }
}
