namespace TravelApi.Models;

public class QuoteVersion
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string ProductType { get; set; } = "General";
    public Currency? Currency { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int QuoteId { get; set; }
    public Quote? Quote { get; set; }

    public bool HasValidTotal() => TotalAmount >= 0m;
}
