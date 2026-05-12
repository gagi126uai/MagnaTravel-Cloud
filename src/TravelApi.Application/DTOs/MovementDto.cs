namespace TravelApi.Application.DTOs;

/// <summary>
/// B1.15 Fase D' (2026-05-11): vista unificada de movimientos financieros.
///
/// Discriminator <see cref="Kind"/> (ver <see cref="TravelApi.Domain.Entities.MovementKinds"/>):
///  - "payment": cobro al cliente (Payment con EntryType="Payment").
///  - "invoice": factura emitida en AFIP (cbteTipo 1=A, 6=B, 11=C, 51=M).
///  - "debit_note": nota de debito emitida en AFIP (cbteTipo 2=A, 7=B, 12=C, 52=M).
///    AGREGADO 2026-05-11 (fix arca-tax-expert): antes caia en "invoice" y la UI
///    ofrecia anular incorrectamente. Las NDs no son anulables desde la UI nueva.
///  - "credit_note": nota de credito emitida en AFIP (cbteTipo 3=A, 8=B, 13=C, 53=M).
///    Las NCs se exponen como su propio kind para no confundirlas con facturas
///    normales en la UI.
///  - "credit_note_reversal": el reversal economico que se crea automaticamente
///    cuando AFIP aprueba una NC (Payment con EntryType="CreditNoteReversal",
///    Amount negativo). Se incluye opcionalmente para auditoria; la UI puede
///    agruparlo bajo la NC parent.
///
/// El campo <see cref="RelatedTo"/> apunta a otro movimiento cuando hay relacion
/// fiscal/logica:
///  - Una NC apunta a la factura origen via OriginalInvoice.
///  - Una ND tambien puede apuntar a una factura origen si se emitio por ajuste.
///  - Un CreditNoteReversal apunta a la NC que lo origino.
/// Permite que el frontend renderee badges "Anula factura #X" sin lookups extra.
/// </summary>
public class MovementDto
{
    public Guid PublicId { get; set; }
    public int LegacyId { get; set; }

    /// <summary>
    /// "payment" | "invoice" | "debit_note" | "credit_note" | "credit_note_reversal".
    /// Ver constantes en <see cref="TravelApi.Domain.Entities.MovementKinds"/>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>Monto en moneda local. Negativo para reversals/NCs si se quiere
    /// representar el efecto economico (NC = baja la deuda).</summary>
    public decimal Amount { get; set; }

    /// <summary>Estado legible (depende del kind):
    /// payment: "Paid"/"Pending"/"Cancelled".
    /// invoice/debit_note/credit_note: "Approved"/"Rejected"/"InProgress"/"Annulled" (deriva de Resultado+AnnulmentStatus).
    /// credit_note_reversal: "Paid".</summary>
    public string Status { get; set; } = string.Empty;

    public Guid? ReservaPublicId { get; set; }
    public int? ReservaLegacyId { get; set; }
    public string? NumeroReserva { get; set; }

    public Guid? CustomerPublicId { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>Referencia legible: Method+Reference para Payment, "Factura B 00001-00000027" para Invoice, etc.</summary>
    public string Reference { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>Trazabilidad de creacion (cuando aplica).</summary>
    public string? CreatedByUserName { get; set; }

    /// <summary>Movimiento relacionado (NC -> factura original, reversal -> NC parent).</summary>
    public MovementRelatedToDto? RelatedTo { get; set; }

    /// <summary>Solo aplica a kind="payment": estado del comprobante asociado
    /// (recibo interno). null si no se emitio. "Issued" | "Voided".
    /// Permite a la UI mostrar el boton "Anular comprobante" cuando corresponda.</summary>
    public string? ReceiptStatus { get; set; }

    /// <summary>Solo aplica a kind="payment" con Receipt emitido: PublicId del receipt.</summary>
    public Guid? ReceiptPublicId { get; set; }
}

public class MovementRelatedToDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid PublicId { get; set; }
    public int LegacyId { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// B1.15 Fase D' (2026-05-11): query params del endpoint /api/movements.
/// Paginado, ordenado por fecha desc + LegacyId desc (tie-breaker estable).
/// </summary>
public class MovementsListQuery : PagedQuery
{
    /// <summary>Filtrar por reserva (LegacyId o public id resolvable).</summary>
    public string? ReservaId { get; set; }

    /// <summary>Filtrar por cliente (LegacyId o public id resolvable).</summary>
    public string? CustomerId { get; set; }

    /// <summary>Comma-separated: "payment,invoice,debit_note,credit_note,credit_note_reversal".</summary>
    public string? Kinds { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    public MovementsListQuery()
    {
        SortBy = "date";
        SortDir = "desc";
    }
}
