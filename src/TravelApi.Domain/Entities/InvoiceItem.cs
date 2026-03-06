using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class InvoiceItem
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; } // Quantity * UnitPrice

    // AFIP VAT ID (3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%)
    public int AlicuotaIvaId { get; set; } 
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteIva { get; set; } // Calculated VAT amount for this item
}
