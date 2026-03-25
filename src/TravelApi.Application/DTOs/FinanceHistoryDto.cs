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
}
