using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

/// <summary>
/// Tarifario Profesional - precios de referencia por proveedor y tipo de servicio
/// Incluye campos dinámicos según el tipo de servicio y estructura de precios completa
/// </summary>
public class Rate
{
    public int Id { get; set; }
    
    // === PROVEEDOR ===
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // === TIPO DE SERVICIO ===
    [Required]
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty; // Aereo, Hotel, Traslado, Paquete, Asistencia, Excursion
    
    // === INFORMACIÓN DEL PRODUCTO ===
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;
    
    [MaxLength(1000)]
    public string? Description { get; set; } // Descripción detallada del servicio
    
    /// <summary>
    /// Unidad de medida del precio: noche, pasajero, servicio, trayecto
    /// </summary>
    [MaxLength(50)]
    public string PriceUnit { get; set; } = "servicio"; // noche, pasajero, servicio, trayecto
    
    // === CAMPOS DINÁMICOS POR TIPO ===
    
    // --- Aéreo ---
    [MaxLength(100)]
    public string? Airline { get; set; } // Nombre de aerolínea
    
    [MaxLength(10)]
    public string? AirlineCode { get; set; } // Código IATA (AA, AR, etc)
    
    [MaxLength(100)]
    public string? Origin { get; set; } // Ciudad/Aeropuerto origen
    
    [MaxLength(100)]
    public string? Destination { get; set; } // Ciudad/Aeropuerto destino
    
    [MaxLength(50)]
    public string? CabinClass { get; set; } // Economy, Business, First
    
    [MaxLength(100)]
    public string? BaggageIncluded { get; set; } // Ej: "23kg + carry-on"
    
    // --- Hotel ---
    [MaxLength(150)]
    public string? HotelName { get; set; }
    
    [MaxLength(100)]
    public string? City { get; set; }
    
    public int? StarRating { get; set; } // 1-5 estrellas
    
    [MaxLength(100)]
    public string? RoomType { get; set; } // Single, Double, Suite, etc
    
    [MaxLength(50)]
    public string? MealPlan { get; set; } // RO, BB, HB, FB, AI
    
    // --- Traslado ---
    [MaxLength(100)]
    public string? PickupLocation { get; set; }
    
    [MaxLength(100)]
    public string? DropoffLocation { get; set; }
    
    [MaxLength(50)]
    public string? VehicleType { get; set; } // Sedan, Van, Bus, etc
    
    public int? MaxPassengers { get; set; }
    
    public bool IsRoundTrip { get; set; } = false;
    
    // --- Paquete ---
    public bool IncludesFlight { get; set; } = false;
    public bool IncludesHotel { get; set; } = false;
    public bool IncludesTransfer { get; set; } = false;
    public bool IncludesExcursions { get; set; } = false;
    public bool IncludesInsurance { get; set; } = false;
    
    public int? DurationDays { get; set; } // Duración del paquete en días
    
    [MaxLength(2000)]
    public string? Itinerary { get; set; } // Descripción del itinerario
    
    // === ESTRUCTURA DE PRECIOS ===
    
    /// <summary>
    /// Costo neto (lo que pagamos al proveedor) - precio unitario
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    /// <summary>
    /// Impuestos incluidos en el costo
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } = 0;
    
    /// <summary>
    /// Precio de venta al público - precio unitario
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    /// <summary>
    /// Comisión calculada (SalePrice - NetCost - Tax)
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; } = 0;
    
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    
    // === VIGENCIA ===
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    
    // === METADATA ===
    public bool IsActive { get; set; } = true;
    
    [MaxLength(500)]
    public string? InternalNotes { get; set; } // Notas internas (no visibles al cliente)
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
