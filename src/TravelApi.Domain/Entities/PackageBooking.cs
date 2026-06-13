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

    // Datos del Paquete. PackageName sigue siendo obligatorio: es la IDENTIDAD visible del paquete
    // (la ficha "producto-primero" siempre lo llena con el texto que tipeo el vendedor).
    [Required]
    [MaxLength(200)]
    public string PackageName { get; set; } = string.Empty;

    // ADR-018 (2026-06-06): Destination deja de ser obligatorio. La ficha "producto-primero"
    // identifica el paquete con PackageName y NO pide un destino aparte. Null = no informado.
    [MaxLength(100)]
    public string? Destination { get; set; }

    public DateTime StartDate { get; set; }

    // ADR-018 (2026-06-06): EndDate pasa a nullable. Cuando la ficha no lo manda, NO se inventa una
    // fecha: los calculos lo coalescen a StartDate (Nights = 0, schedule usa StartDate como fin).
    public DateTime? EndDate { get; set; }

    public int Nights { get; set; }
    
    // Qué incluye
    public bool IncludesHotel { get; set; } = true;
    public bool IncludesFlight { get; set; } = true;
    public bool IncludesTransfer { get; set; } = false;
    public bool IncludesExcursions { get; set; } = false;
    public bool IncludesMeals { get; set; } = false;
    
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;

    /// <summary>
    /// Ficha F2 (guia-ux-gaston): base de ocupacion que define la tarifa por persona del paquete
    /// ("double" = base doble, "triple" = base triple, etc). Metadato operativo, NO afecta costos ni
    /// saldo (el precio ya viene en SalePrice). Null = legacy / no informado. Antes el front lo metia
    /// a la fuerza en Notes (pisaba la nota real).
    /// </summary>
    [MaxLength(20)]
    public string? OccupancyBase { get; set; }

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
    /// Auditoria ERP 2026-06-12 (item 5): VUELVE la fecha limite de pago al operador (la carga el
    /// operador por servicio). Mismo criterio y misma justificacion que
    /// <see cref="HotelBooking.OperatorPaymentDeadline"/> — ADR-019 dropeo este campo porque murio la
    /// pill manual vieja, NO el concepto. Alimenta la alarma de pago al operador en AlertService.
    /// Date-only "de pared" Kind=Utc. Opcional (null = no informada).
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

    // === ADR-020 (2026-06-07): trazabilidad de confirmacion del operador y de cancelacion del servicio ===

    /// <summary>
    /// ADR-020: fecha en que el operador CONFIRMO este servicio (la estampa el motor de estados).
    /// Null = nunca confirmado. NO se borra al des-confirmar. Gobierna borrar-vs-cancelar y penalidades.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>ADR-020: cuando se cancelo el servicio (Status -> Cancelado). Null = no cancelado.</summary>
    public DateTime? CancelledAt { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserId { get; set; }

    [MaxLength(200)]
    public string? CancelledByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int GetExpectedPaxCount() => Adults + Children;
}
