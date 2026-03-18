using System;
using System.Text.Json.Serialization;

namespace TravelApi.Domain.Entities;

public class ReservaAttachment
{
    public int Id { get; set; }
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public string? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
