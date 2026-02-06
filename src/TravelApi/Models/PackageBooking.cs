using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class PackageBooking
{
    public int Id { get; set; }
    
    // Relaciones
    public int TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Datos del Paquete
    [Required]
    [MaxLength(200)]
    public string PackageName { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(100)]
    public string Destination { get; set; } = string.Empty;
    
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int Nights { get; set; }
    
    // Qué incluye
    public bool IncludesHotel { get; set; } = true;
    public bool IncludesFlight { get; set; } = true;
    public bool IncludesTransfer { get; set; } = false;
    public bool IncludesExcursions { get; set; } = false;
    public bool IncludesMeals { get; set; } = false;
    
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    
    [MaxLength(2000)]
    public string? Itinerary { get; set; } // Descripción del itinerario
    
    // Confirmación
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }
    
    [MaxLength(50)]
    public string Status { get; set; } = "Solicitado";
    
    // Financiero (copiado del tarifario - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }
    
    [MaxLength(500)]
    public string? Notes { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
