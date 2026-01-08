namespace TravelApi.Models;

public class BspNormalizedRecord
{
    public int Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string ReservationReference { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal BaseAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public int BspImportBatchId { get; set; }
    public BspImportBatch? BspImportBatch { get; set; }
    public BspReconciliationEntry? ReconciliationEntry { get; set; }
}
