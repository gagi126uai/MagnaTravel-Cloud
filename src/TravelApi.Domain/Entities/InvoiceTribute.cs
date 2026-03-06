using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class InvoiceTribute
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    // AFIP Tribute ID (e.g., 99=IIBB, 1=Impuestos Nacionales)
    public int TributeId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal BaseImponible { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Alicuota { get; set; } // Percentage

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }
}
