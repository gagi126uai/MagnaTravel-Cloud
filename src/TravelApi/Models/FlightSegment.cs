using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class FlightSegment
{
    public int Id { get; set; }
    
    // Relaciones
    public int TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Datos del vuelo
    [Required]
    [MaxLength(3)]
    public string AirlineCode { get; set; } = string.Empty; // AA, AR, LA
    
    [MaxLength(100)]
    public string? AirlineName { get; set; } // American Airlines
    
    [Required]
    [MaxLength(10)]
    public string FlightNumber { get; set; } = string.Empty; // 900
    
    [Required]
    [MaxLength(3)]
    public string Origin { get; set; } = string.Empty; // MIA
    
    [MaxLength(100)]
    public string? OriginCity { get; set; } // Miami
    
    [Required]
    [MaxLength(3)]
    public string Destination { get; set; } = string.Empty; // EZE
    
    [MaxLength(100)]
    public string? DestinationCity { get; set; } // Buenos Aires
    
    public DateTime DepartureTime { get; set; }
    public DateTime ArrivalTime { get; set; }
    
    // Clase y equipaje
    [MaxLength(20)]
    public string CabinClass { get; set; } = "Economy"; // Economy, Premium Economy, Business, First
    
    [MaxLength(50)]
    public string? Baggage { get; set; } // "23kg" o "2PC"
    
    // Ticket
    [MaxLength(50)]
    public string? TicketNumber { get; set; }
    
    [MaxLength(20)]
    public string? FareBase { get; set; } // Base tarifaria
    
    [MaxLength(20)]
    public string? PNR { get; set; } // Record locator
    
    [MaxLength(2)]
    public string Status { get; set; } = "HK"; // HK, HL, UC, UN
    
    // Financiero (copiado del tarifario - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } // Impuestos
    
    [MaxLength(500)]
    public string? Notes { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Legacy - mantener compatibilidad temporal
    public int? ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
}
