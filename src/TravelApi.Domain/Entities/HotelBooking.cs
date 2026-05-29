using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class HotelBooking : IHasPublicId
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

    // Datos del Hotel
    [Required]
    [MaxLength(200)]
    public string HotelName { get; set; } = string.Empty;
    
    public int? StarRating { get; set; } // 1-5
    
    [MaxLength(200)]
    public string? Address { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;
    
    [MaxLength(100)]
    public string? Country { get; set; }
    
    // Fechas
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    
    // Habitación
    [MaxLength(50)]
    public string RoomType { get; set; } = "Doble"; // Single, Doble, Triple, Suite
    
    [MaxLength(50)]
    public string MealPlan { get; set; } = "Desayuno"; // Solo Aloj., Desayuno, Media Pensión, All Inclusive
    
    public int Rooms { get; set; } = 1;
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    
    // Confirmación
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }
    
    [MaxLength(50)]
    public string Status { get; set; } = "Solicitado";
    
    // Financiero (copiado del tarifario al momento de crear - inmutable)
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; }

    /// <summary>
    /// Moneda en que se COTIZO el servicio (copiada del tarifario al crearlo).
    /// Es metadato de TRAZABILIDAD: NO se usa todavia en calculos de saldo, pagos ni factura.
    /// Null = legacy / no informado (se asume ARS por compatibilidad hacia atras).
    /// </summary>
    [MaxLength(3)]
    public string? Currency { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Planner de habitaciones: JSON con asignación de pasajeros por habitación
    public string? RoomingAssignmentsJson { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009 §2.3.2, 2026-05-21): lista JSON de conceptos no reintegrables
    /// que se imputan al cliente fuera del costo neto/venta del hotel
    /// (ej. cargo gestion $5.000, seguro cancelacion $20.000).
    /// Persistido como <c>jsonb</c> (consistencia con
    /// <c>Supplier.PenaltyPolicyJson</c>).
    ///
    /// <para>Schema:
    /// <c>[{"description": string, "amount": decimal, "category": InvoiceItemCategory}, ...]</c>
    /// Null o array vacio significa "sin conceptos adicionales".</para>
    ///
    /// <para>Cada concepto se traduce a un <c>InvoiceItem</c> con
    /// <c>IsRefundable=false</c> al momento de facturar, asi se mantiene la
    /// trazabilidad fiscal del concepto retenido en la NC parcial.</para>
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? NonRefundableConceptsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int GetExpectedPaxCount() => Adults + Children;
}
