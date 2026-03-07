using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class LeadStatus
{
    public const string New = "Nuevo";
    public const string Contacted = "Contactado";
    public const string Quoted = "Cotizado";
    public const string Won = "Ganado";
    public const string Lost = "Perdido";
}

public class Lead
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;
    
    [MaxLength(200)]
    public string? Email { get; set; }
    
    [MaxLength(50)]
    public string? Phone { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = LeadStatus.New;
    
    [MaxLength(50)]
    public string? Source { get; set; } // Web, WhatsApp, Referido, Teléfono, Instagram, Otro
    
    [MaxLength(200)]
    public string? InterestedIn { get; set; } // Destino o producto de interés
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal EstimatedBudget { get; set; } = 0;
    
    [MaxLength(2000)]
    public string? Notes { get; set; }
    
    // Assignment
    public string? AssignedToUserId { get; set; }
    
    [MaxLength(200)]
    public string? AssignedToName { get; set; }
    
    // Follow-up
    public DateTime? NextFollowUp { get; set; }
    
    // Conversion
    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }
    
    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    
    // Navigation
    public ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();
}
