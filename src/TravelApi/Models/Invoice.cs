using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class Invoice
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AFIP Data
    public int TipoComprobante { get; set; } // 1 (A), 6 (B), 11 (C)
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    
    public string? CAE { get; set; }
    public DateTime? VencimientoCAE { get; set; }
    
    public string? Resultado { get; set; } // A (Aprobado), R (Rechazado), P (Parcial)
    public string? Observaciones { get; set; } // Error messages from AFIP

    // Financial Data
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteTotal { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteNeto { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteIva { get; set; }
    
    // Relationships
    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

    // Optional: Link to specific payments if we bill per payment
    // For now, let's assume we bill an amount linked to a File.
}
