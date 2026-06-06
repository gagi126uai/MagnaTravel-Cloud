using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class TransferBooking : IHasPublicId
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

    /// <summary>
    /// ADR-018 (2026-06-06): nombre del producto tal como lo VIO/tipeo el vendedor en la ficha
    /// "producto-primero" (ej. "Traslado privado EZE-Hotel"). Es la IDENTIDAD visible del traslado.
    /// Snapshot: se copia al crear y NO se re-deriva del Rate (igual que <see cref="FlightSegment.ProductName"/>).
    /// Null = carga por el modal viejo (la fila se muestra con la ruta Pickup->Dropoff o el vehiculo).
    /// </summary>
    [MaxLength(200)]
    public string? ProductName { get; set; }

    // Datos del Transfer. ADR-018: Pickup/Dropoff dejan de ser obligatorios (la ficha
    // "producto-primero" usa un solo texto = ProductName). Null = no informado.
    [MaxLength(200)]
    public string? PickupLocation { get; set; }

    [MaxLength(200)]
    public string? DropoffLocation { get; set; }
    
    public DateTime PickupDateTime { get; set; }
    
    [MaxLength(20)]
    public string? FlightNumber { get; set; } // Vuelo asociado
    
    [MaxLength(50)]
    public string VehicleType { get; set; } = "Sedan"; // Sedan, Van, Minibus, Bus

    /// <summary>
    /// Ficha F2 (guia-ux-gaston): sentido del traslado. "in" = llegada (del aeropuerto al hotel),
    /// "out" = salida (del hotel al aeropuerto). Metadato operativo, NO afecta costos ni saldo.
    /// Null = legacy / no informado. Antes el front lo metia a la fuerza en Notes (pisaba la nota real).
    /// </summary>
    [MaxLength(20)]
    public string? Direction { get; set; }

    /// <summary>
    /// Ficha F2 (guia-ux-gaston): modalidad del servicio. "private" = privado, "shared" = compartido.
    /// Metadato operativo, NO afecta costos ni saldo. Null = legacy / no informado.
    /// Antes el front lo metia a la fuerza en Notes (pisaba la nota real).
    /// </summary>
    [MaxLength(20)]
    public string? ServiceMode { get; set; }

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

    // ADR-017 F1.1 (2026-06-05): Traslado NO lleva fecha limite (la tabla de campos por tipo del
    // mockup no la incluye — no inventamos alcance). Solo la marca "costo a confirmar".

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

    public int GetExpectedPaxCount() => Passengers;
}
