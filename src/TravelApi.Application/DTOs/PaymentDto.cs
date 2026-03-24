namespace TravelApi.Application.DTOs;

public class PaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "Paid";
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public bool AffectsCash { get; set; }
    public Guid? RelatedInvoicePublicId { get; set; }
    public Guid? OriginalPaymentPublicId { get; set; }
    public PaymentReceiptDto? Receipt { get; set; }
}
