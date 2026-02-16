using System;
using System.Text.Json.Serialization;

namespace TravelApi.Models;

public class TravelFileAttachment
{
    public int Id { get; set; }

    public int TravelFileId { get; set; }
    [JsonIgnore]
    public TravelFile? TravelFile { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty; // Guid-based name on disk
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string UploadedBy { get; set; } = string.Empty; // Username/Email of uploader
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
