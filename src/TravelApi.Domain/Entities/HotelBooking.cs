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
    
    [MaxLength(500)]
    public string? Notes { get; set; }
    
    // Planner de habitaciones: JSON con asignación de pasajeros por habitación
    public string? RoomingAssignmentsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int GetExpectedPaxCount() => Adults + Children;
}
