using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class QuoteItem
{
    public int Id { get; set; }
    
    public int QuoteId { get; set; }
    public Quote? Quote { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string ServiceType { get; set; } = "Hotel"; // Hotel, Vuelo, Transfer, Paquete, Excursión, Seguro, Otro
    
    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;
    
    // Optional supplier link
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    public int Quantity { get; set; } = 1;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal MarkupPercent { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost => UnitCost * Quantity;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPrice => UnitPrice * Quantity;

    [MaxLength(500)]
    public string? Notes { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
