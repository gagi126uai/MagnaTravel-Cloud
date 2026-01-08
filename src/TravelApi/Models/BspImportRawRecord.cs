namespace TravelApi.Models;

public class BspImportRawRecord
{
    public int Id { get; set; }
    public int LineNumber { get; set; }
    public string RawContent { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int BspImportBatchId { get; set; }
    public BspImportBatch? BspImportBatch { get; set; }
}
