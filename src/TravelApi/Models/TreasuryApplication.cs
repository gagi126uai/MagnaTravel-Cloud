namespace TravelApi.Models;

public class TreasuryApplication
{
    public int Id { get; set; }
    public decimal AmountApplied { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    public int TreasuryReceiptId { get; set; }
    public TreasuryReceipt? TreasuryReceipt { get; set; }

    public int ReservationId { get; set; }
    public Reservation? Reservation { get; set; }

    public bool HasValidAmount() => AmountApplied > 0m;
}
