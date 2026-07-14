using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): UNA vuelta del ciclo "la Nota de Debito de la multa
/// salio con CAE y estaba mal -> se emite una Nota de Credito que la anula fiscalmente". Es una fila POR EVENTO
/// de deshacer (molde de <see cref="BookingCancellationCreditNote"/>, ADR-042): la ND sigue siendo <c>Issued</c>
/// en <see cref="BookingCancellation"/> hasta que ESTA anulacion consigue su propio CAE; el panel de la ficha
/// deriva el paso mirando "¿hay una fila hija viva (Pending) o la ultima quedo Failed para la ND de este BC?".
///
/// <para><b>Por que una tabla hija y no escalares en el BC padre</b>: el ciclo emitir-multa -&gt; deshacer -&gt;
/// re-emitir se puede repetir (RG 4540 no pone tope de cantidad, ver la spec fiscal). Con escalares, la 2da
/// vuelta pisaria el rastro de la 1ra. La tabla historiza cada vuelta con su motivo y su par NC&lt;-&gt;ND, y el
/// indice unico filtrado de abajo impide dos anulaciones vivas a la vez sobre la MISMA ND.</para>
/// </summary>
public class BookingCancellationDebitNoteAnnulment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al BC duenio de la multa que se esta deshaciendo. Cascade (la fila no tiene sentido sin su BC).</summary>
    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    /// <summary>
    /// FK a la Nota de Debito (Invoice tipo 2/7/12) que se esta anulando. Es una FOTO de "cual ND": si el ciclo se
    /// repite, cada vuelta apunta a la ND VIVA de ese momento (la vieja ya quedo neutralizada por su propia NC).
    /// </summary>
    public int AnnulledDebitNoteInvoiceId { get; set; }
    public Invoice AnnulledDebitNoteInvoice { get; set; } = null!;

    /// <summary>
    /// FK a la Nota de Credito que anula la ND de arriba. Null hasta que <c>InvoiceService.CreateAsync</c> la crea
    /// (el CAE llega despues, async, via <c>ProcessInvoiceJob</c>).
    /// </summary>
    public int? AnnulmentCreditNoteInvoiceId { get; set; }
    public Invoice? AnnulmentCreditNoteInvoice { get; set; }

    /// <summary>Sigue el CAE de la Nota de Credito. Default Pending (recien encolada).</summary>
    public DebitNoteAnnulmentStatus Status { get; set; } = DebitNoteAnnulmentStatus.Pending;

    /// <summary>Motivo OBLIGATORIO por el que se deshace la multa (regla dura #14 de la spec fiscal: auditoria del contador).</summary>
    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    // ---- Snapshot espejo de la ND al momento de deshacer (regla dura #3/#4: la NC es el negativo EXACTO) ----

    /// <summary>Importe total de la ND anulada, congelado. Es el importe que la NC-anula-ND reversa (100%, nunca parcial).</summary>
    public decimal Amount { get; set; }

    /// <summary>Moneda ISO 4217 de la ND anulada (espejo de <see cref="BookingCancellation.PenaltyCurrencyAtEvent"/> proyectada a ISO).</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>TC congelado de la ND anulada (<c>Invoice.MonCotiz</c>), null en pesos. La NC hereda este MISMO TC, nunca recotiza.</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,6)")]
    public decimal? ExchangeRate { get; set; }

    // ---- Auditoria (regla dura #14: quien / cuando / motivo ya cubierto arriba) ----

    [MaxLength(450)]
    public string RequestedByUserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? RequestedByUserName { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Mensaje que ARCA devolvio si la NC-anula-ND fallo su CAE (Status=Failed). Truncado a 1000, texto tecnico: nunca al usuario final tal cual.</summary>
    [MaxLength(1000)]
    public string? ArcaErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
