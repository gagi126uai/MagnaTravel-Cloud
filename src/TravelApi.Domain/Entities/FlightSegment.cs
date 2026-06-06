using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class FlightSegment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // Relaciones
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    
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

    // Numero de confirmacion que devuelve la aerolinea/operador para este segmento.
    // Es distinto del PNR/localizador (que sirve para gestionar la reserva en la GDS):
    // este campo guarda el comprobante de confirmacion que entrega el proveedor.
    // Nullable porque hay segmentos todavia "Solicitados" sin confirmar.
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }

    // Cantidad de pasajeros que viajan EN ESTE segmento concreto.
    // No siempre coincide con el total de pasajeros de la reserva: en un mismo file
    // puede haber tramos con distinta cantidad de gente (ej. uno viaja solo la ida).
    // Nullable: los segmentos cargados antes de este campo quedan en null (no informado).
    public int? PassengerCount { get; set; }

    // Financiero (copiado del tarifario - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } // Impuestos

    /// <summary>
    /// Moneda en que se COTIZO el servicio (copiada del tarifario al crearlo).
    /// Es metadato de TRAZABILIDAD: NO se usa todavia en calculos de saldo, pagos ni factura.
    /// Null = legacy / no informado (se asume ARS por compatibilidad hacia atras).
    /// </summary>
    [MaxLength(3)]
    public string? Currency { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (2026-06-05): fecha limite de EMISION del ticket. Aditivo, nullable (filas
    /// existentes en null). El deadline conceptual es del PNR, pero la entidad es por segmento: la
    /// ficha de carga (F2) escribe el mismo valor en los segmentos de la operacion y la alerta (F3)
    /// agrupa por (ReservaId, PNR) con MIN(TicketingDeadline). En F1.1 nadie lo escribe (persistencia
    /// gobernada por DeadlinesSpecified = F1.4). Date-only "de pared".
    /// </summary>
    public DateTime? TicketingDeadline { get; set; }

    /// <summary>
    /// ADR-017 F1.1 (decision D7): marca "costo a confirmar" (default false, ortogonal al workflow).
    /// Mismo criterio que <see cref="HotelBooking.CostToConfirm"/>. En F1.1 nadie lo setea.
    /// </summary>
    public bool CostToConfirm { get; set; } = false;

    /// <summary>
    /// ADR-017 F1.1 (D7): razon de la marca ("NoKnownCost" | "StaleReference"). Null si no hay marca.
    /// </summary>
    [MaxLength(30)]
    public string? CostToConfirmReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Tarifario - snapshot de precios al momento de crear
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }

    // Legacy - mantener compatibilidad temporal
    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }
}
