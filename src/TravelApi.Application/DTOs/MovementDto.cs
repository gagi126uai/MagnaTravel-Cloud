namespace TravelApi.Application.DTOs;

/// <summary>
/// B1.15 Fase D' (2026-05-11): vista unificada de movimientos financieros.
///
/// Discriminator <see cref="Kind"/>:
///  - "payment": cobro al cliente (Payment con EntryType="Payment").
///  - "invoice": factura emitida en AFIP (Invoice).
///  - "credit_note": nota de credito emitida en AFIP (Invoice con TipoComprobante
///    de NC). Las NCs se exponen como su propio kind para no confundirlas con
///    facturas normales en la UI.
///  - "credit_note_reversal": el reversal economico que se crea automáticamente
///    cuando AFIP aprueba una NC (Payment con EntryType="CreditNoteReversal",
///    Amount negativo). Se incluye opcionalmente para auditoria; la UI puede
///    agruparlo bajo la NC parent.
///
/// El campo <see cref="RelatedTo"/> apunta a otro movimiento cuando hay relacion
/// fiscal/lógica:
///  - Una NC apunta a la factura origen via OriginalInvoice.
///  - Un CreditNoteReversal apunta a la NC que lo originó.
/// Permite que el frontend renderee badges "Anula factura #X" sin lookups extra.
/// </summary>
public class MovementDto
{
    public Guid PublicId { get; set; }
    public int LegacyId { get; set; }

    /// <summary>"payment" | "invoice" | "credit_note" | "credit_note_reversal".</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>Monto en moneda local. Negativo para reversals/NCs si se quiere
    /// representar el efecto economico (NC = baja la deuda).</summary>
    public decimal Amount { get; set; }

    /// <summary>Estado legible (depende del kind):
    /// payment: "Paid"/"Pending"/"Cancelled".
    /// invoice/credit_note: "Approved"/"Rejected"/"InProgress"/"Annulled" (deriva de Resultado+AnnulmentStatus).
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

    /// <summary>Comma-separated: "payment,invoice,credit_note,credit_note_reversal".</summary>
    public string? Kinds { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    public MovementsListQuery()
    {
        SortBy = "date";
        SortDir = "desc";
    }
}
