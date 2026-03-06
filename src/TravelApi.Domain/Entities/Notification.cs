using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class Notification
{
    public int Id { get; set; }
    
    [Required]
    public string UserId { get; set; } = string.Empty; // Who triggered it
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public string Type { get; set; } = "Info"; // Success, Error, Info, Warning
    
    public bool IsRead { get; set; } = false;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Optional: link to entity
    public int? RelatedEntityId { get; set; }
    public string? RelatedEntityType { get; set; } // "Invoice", "File", etc.
}
