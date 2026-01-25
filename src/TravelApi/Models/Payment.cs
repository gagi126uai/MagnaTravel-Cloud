using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class Payment
{
    public int Id { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card

    public string Status { get; set; } = "Paid"; // Paid, Pending, Cancelled

    public string? Notes { get; set; }

    // Direct link to TravelFile (preferred)
    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

    // Legacy link via Reservation (for backwards compatibility)
    public int? ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
}
