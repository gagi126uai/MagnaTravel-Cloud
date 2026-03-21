using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class QuoteStatus
{
    public const string Draft = "Borrador";
    public const string Sent = "Enviada";
    public const string Accepted = "Aceptada";
    public const string Expired = "Vencida";
    public const string Rejected = "Rechazada";
}

public class Quote
{
    public int Id { get; set; }
    
    [MaxLength(50)]
    public string QuoteNumber { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    
    [MaxLength(2000)]
    public string? Description { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = QuoteStatus.Draft;
    
    // Client
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // Commercial traceability
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    
    // Dates
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }
    public DateTime? AcceptedAt { get; set; }
    
    // Travel Dates
    public DateTime? TravelStartDate { get; set; }
    public DateTime? TravelEndDate { get; set; }
    
    [MaxLength(200)]
    public string? Destination { get; set; }
    
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    
    // Financials (calculated from items)
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost { get; set; } = 0;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSale { get; set; } = 0;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossMargin { get; set; } = 0;
    
    // Converted to Reserva?
    public int? ConvertedReservaId { get; set; }
    public Reserva? ConvertedReserva { get; set; }
    
    [MaxLength(500)]
    public string? Notes { get; set; }
    
    // Navigation
    public ICollection<QuoteItem> Items { get; set; } = new List<QuoteItem>();
}
