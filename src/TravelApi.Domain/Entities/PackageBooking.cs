using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class PackageBooking : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // Relaciones
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    // Tarifario - snapshot de precios al momento de crear
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }

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

    /// <summary>
    /// Impuestos incluidos en el costo (mismo criterio que <see cref="FlightSegment.Tax"/> y
    /// <see cref="Rate.Tax"/>): NO suma al precio que paga el cliente (SalePrice ya es el total),
    /// es un componente del costo. La ganancia/Commission = SalePrice - NetCost - Tax.
    /// Default 0 = sin impuesto informado (las filas previas a este campo quedan en 0, lo que deja
    /// la Commission inalterada porque SalePrice - NetCost - 0 = SalePrice - NetCost).
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; }

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
    /// ADR-017 F1.1 (2026-06-05): fecha limite de seña/pago al operador. Aditivo, nullable (filas
    /// existentes en null). En F1.1 nadie lo escribe (ficha = F2; persistencia gobernada por
    /// DeadlinesSpecified = F1.4). Date-only "de pared" igual que <see cref="StartDate"/>.
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }

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

    public int GetExpectedPaxCount() => Adults + Children;
}
