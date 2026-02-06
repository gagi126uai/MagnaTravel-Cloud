using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class TransferBooking
{
    public int Id { get; set; }
    
    // Relaciones
    public int TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Datos del Transfer
    [Required]
    [MaxLength(200)]
    public string PickupLocation { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(200)]
    public string DropoffLocation { get; set; } = string.Empty;
    
    public DateTime PickupDateTime { get; set; }
    
    [MaxLength(20)]
    public string? FlightNumber { get; set; } // Vuelo asociado
    
    [MaxLength(50)]
    public string VehicleType { get; set; } = "Sedan"; // Sedan, Van, Minibus, Bus
    
    public int Passengers { get; set; } = 1;
    
    public bool IsRoundTrip { get; set; } = false;
    public DateTime? ReturnDateTime { get; set; }
    
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
