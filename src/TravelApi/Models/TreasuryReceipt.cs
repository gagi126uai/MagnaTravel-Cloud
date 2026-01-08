using System.Linq;

namespace TravelApi.Models;

public class TreasuryReceipt
{
    public int Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Method { get; set; } = "Transfer";
    public Currency? Currency { get; set; }
    public decimal Amount { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }

    public ICollection<TreasuryApplication> Applications { get; set; } = new List<TreasuryApplication>();

    public decimal AppliedAmount => Applications.Sum(application => application.AmountApplied);
    public decimal RemainingAmount => Amount - AppliedAmount;

    public bool HasValidAmount() => Amount >= 0m;
}
