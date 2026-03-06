using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class AuditLog
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    public string? UserName { get; set; } // Opcional, snapshot del nombre al momento del cambio

    [Required]
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty; // Create, Update, Delete

    [Required]
    [MaxLength(100)]
    public string EntityName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string EntityId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? Changes { get; set; } // JSON: { "Field": { "Old": "Val1", "New": "Val2" } }
}
