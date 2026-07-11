namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// ADR-009/ADR-025 (read-model bandeja "NC por revisar", 2026-06-13): fila de la bandeja dentro de
/// Cobranza que lista las cancelaciones cuya NOTA DE CREDITO esta esperando revision/emision manual.
///
/// <para>Una <c>BookingCancellation</c> entra a esta bandeja cuando el flujo de NC parcial (ADR-009)
/// clasifica el caso como "requiere revision manual" y lo deja en
/// <c>BookingCancellationStatus.ManualReviewPending</c> (estado 9) con un <c>ApprovalRequest</c> abierto.
/// El estado transitorio <c>RequiresManualReview</c> (8) NO se persiste bajo el flujo normal (es un
/// marker del enum), pero la bandeja lo contempla por completitud.</para>
///
/// <para>Es un DTO de lectura PLANO (no expone la entidad de persistencia). Solo lleva lo que la bandeja
/// necesita mostrar; nada sensible del pasajero. El monto es el de la liquidacion fiscal bajo revision
/// (la NC que el back-office tiene que aprobar/emitir), visible solo con el permiso de la bandeja
/// (<c>cobranzas.invoice_annul</c>), mismo precedente que <c>CancellationDebitNotePendingDto</c>.</para>
/// </summary>
public class PendingCreditNoteReviewDto
{
    /// <summary>PublicId del BookingCancellation (la UI navega por PublicId, nunca por Id int).</summary>
    public Guid BookingCancellationPublicId { get; set; }

    /// <summary>PublicId de la reserva padre (para que la bandeja linkee al detalle de la reserva).</summary>
    public Guid ReservaPublicId { get; set; }

    /// <summary>Numero de reserva (legible para el operador de la bandeja).</summary>
    public string ReservaNumero { get; set; } = string.Empty;

    /// <summary>
    /// Nombre legible de la reserva/cliente. Toma el nombre del cliente pagador (<c>Payer.FullName</c>)
    /// si existe; sino cae al nombre de la reserva. No expone documento ni datos sensibles del pasajero.
    /// </summary>
    public string ClienteNombre { get; set; } = string.Empty;

    /// <summary>
    /// Etiqueta de negocio en español del estado del pendiente, YA saneada para el usuario (nunca el nombre
    /// crudo del enum interno). Valores: "Pendiente de emisión" (cancelación parcial de un servicio cuya nota
    /// de crédito todavía no se emitió) o "En revisión" (liquidación que el back-office tiene que aprobar/emitir).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Cuando el pendiente entro a la bandeja, para ordenar por antiguedad. Para las liquidaciones en revision
    /// manual es <c>ConfirmedWithClientAt</c> (sellado al transicionar a <c>ManualReviewPending</c>); para una
    /// cancelacion parcial pendiente de emision (BC en Drafted, sin ese timestamp) es <c>DraftedAt</c>.
    /// </summary>
    public DateTime? EnteredReviewAt { get; set; }

    /// <summary>
    /// Monto de la NC parcial bajo revision (lo que el back-office tiene que aprobar/emitir). Sale de
    /// <c>FiscalLiquidation.FiscalAmountToCredit</c>; null si el BC todavia no tiene la liquidacion
    /// poblada (caso borde de datos legacy). Es un monto fiscal, no un costo: se expone bajo el permiso
    /// de la bandeja, igual que la penalidad de la bandeja de notas de debito.
    /// </summary>
    public decimal? CreditNoteAmount { get; set; }

    /// <summary>Moneda de la liquidacion (ISO 4217, ARS en el MVP). Null si no hay liquidacion poblada.</summary>
    public string? CreditNoteCurrency { get; set; }
}
