using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class LeadActivity : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = "Nota"; // Llamada, Email, WhatsApp, Reunión, Nota, Cotización
    
    [Required]
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;
    
    [MaxLength(200)]
    public string? CreatedBy { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
