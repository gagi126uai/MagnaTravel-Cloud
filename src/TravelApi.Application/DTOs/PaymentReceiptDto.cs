namespace TravelApi.Application.DTOs;

public class PaymentReceiptDto
{
    public int Id { get; set; }
    public int PaymentId { get; set; }
    public int ReservaId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? VoidedAt { get; set; }
}
