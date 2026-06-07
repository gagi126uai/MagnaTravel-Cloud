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
    
    /// <summary>
    /// ADR-018 (2026-06-06): nombre del producto tal como lo VIO/tipeo el vendedor en la ficha
    /// "producto-primero" (ej. "AEP-IGR LATAM"). Es la IDENTIDAD visible del segmento. Snapshot:
    /// se copia al crear y NO se re-deriva del Rate despues (preserva el principio de ADR-017 §6,
    /// igual que <see cref="HotelBooking.HotelName"/>). Null = carga por el modal viejo (la fila
    /// se sigue mostrando con la derivacion estructurada AirlineCode/FlightNumber/ruta).
    /// </summary>
    [MaxLength(200)]
    public string? ProductName { get; set; }

    // Datos del vuelo. ADR-018: estos 4 campos estructurados dejan de ser obligatorios. La ficha
    // "producto-primero" identifica el vuelo con un solo texto (ProductName) y NO pide aerolinea/
    // nro/origen/destino por separado; el modal viejo los sigue mandando. Null = no informado.
    [MaxLength(3)]
    public string? AirlineCode { get; set; } // AA, AR, LA

    [MaxLength(100)]
    public string? AirlineName { get; set; } // American Airlines

    [MaxLength(10)]
    public string? FlightNumber { get; set; } // 900

    [MaxLength(3)]
    public string? Origin { get; set; } // MIA

    [MaxLength(100)]
    public string? OriginCity { get; set; } // Miami

    [MaxLength(3)]
    public string? Destination { get; set; } // EZE
    
    [MaxLength(100)]
    public string? DestinationCity { get; set; } // Buenos Aires
    
    public DateTime DepartureTime { get; set; }
    public DateTime ArrivalTime { get; set; }
    
    // Clase y equipaje. ADR-018 Ronda 7 (2026-06-06): la cabina deja de ser obligatoria —
    // null = "Sin especificar" (antes era NOT NULL con default "Economy"; la columna se
    // relaja en la migracion Adr017_M6).
    [MaxLength(20)]
    public string? CabinClass { get; set; } // Economy, Premium Economy, Business, First
    
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

    // (ADR-019, 2026-06-06: aca vivia TicketingDeadline — fecha limite manual de emision de ADR-017
    // F1.4, nunca prendida en prod. La reemplazo el aviso automatico "Proximos inicios"; columna
    // dropeada en la migracion Adr019_M1.)

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
