namespace TravelApi.Application.DTOs;

public class FinanceHistoryItemDto
{
    public Guid PublicId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public decimal Amount { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? Reference { get; set; }
    public string? Method { get; set; }
    public string? PaymentEntryType { get; set; }
    public Guid? ReceiptPublicId { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? ReceiptStatus { get; set; }
    public int? InvoiceTipoComprobante { get; set; }
    public string? InvoiceResultado { get; set; }
    public bool InvoiceWasForced { get; set; }
    public string? InvoiceForceReason { get; set; }
    public string? MovementSourceType { get; set; }
    public string? MovementDirection { get; set; }
    public bool IsManual { get; set; }

    // ADR-023 T2: campos ADITIVOS. Se agregan SIN romper los previos (el front actual sigue
    // leyendo los de arriba; el retoque visual va despues con UX gate).
    /// <summary>Moneda REAL del movimiento (cobro/pago/manual), tal como entro/salio de caja. null = fila de comprobante (factura/NC, sin moneda de caja).</summary>
    public string? Currency { get; set; }
    /// <summary>true = fila de ANULACION (contra-asiento del libro). El soft-delete ya no esconde la anulacion: se ve el cobro y su reversa.</summary>
    public bool IsReversal { get; set; }
    /// <summary>true = el monto fue ocultado por falta de cobranzas.see_cost (egreso de costo enmascarado).</summary>
    public bool AmountMasked { get; set; }
    /// <summary>
    /// SourceType CRUDO del asiento del libro (espejo de <c>CashMovementDto.LedgerSourceType</c>). Obligatorio
    /// (review B2): el enmascarado de costo decide SIEMPRE sobre este valor crudo, NUNCA sobre el
    /// <see cref="MovementSourceType"/> colapsado del front. null en filas de comprobante.
    /// </summary>
    public string? LedgerSourceType { get; set; }
}
