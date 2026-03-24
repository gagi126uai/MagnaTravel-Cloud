namespace TravelApi.Application.DTOs;

public class PaymentReceiptDto
{
    public Guid PublicId { get; set; }
    public Guid PaymentPublicId { get; set; }
    public Guid ReservaPublicId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? VoidedAt { get; set; }
}
