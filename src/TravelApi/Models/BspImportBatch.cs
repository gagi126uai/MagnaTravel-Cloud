namespace TravelApi.Models;

public class BspImportBatch
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Open";
    public DateTime? ClosedAt { get; set; }

    public ICollection<BspImportRawRecord> RawRecords { get; set; } = new List<BspImportRawRecord>();
    public ICollection<BspNormalizedRecord> NormalizedRecords { get; set; } = new List<BspNormalizedRecord>();
    public ICollection<BspReconciliationEntry> Reconciliations { get; set; } = new List<BspReconciliationEntry>();
}
