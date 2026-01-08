namespace TravelApi.Models;

public class BspReconciliationEntry
{
    public int Id { get; set; }
    public string Status { get; set; } = "Pending";
    public decimal? DifferenceAmount { get; set; }
    public DateTime ReconciledAt { get; set; } = DateTime.UtcNow;

    public int BspImportBatchId { get; set; }
    public BspImportBatch? BspImportBatch { get; set; }

    public int BspNormalizedRecordId { get; set; }
    public BspNormalizedRecord? BspNormalizedRecord { get; set; }

    public int? ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
}
