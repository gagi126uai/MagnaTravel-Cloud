namespace TravelApi.Application.DTOs;

public class PaymentDto
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "Paid";
    public int? TravelFileId { get; set; }
    public string? FileNumber { get; set; }
}
