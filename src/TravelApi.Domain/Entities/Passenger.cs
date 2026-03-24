using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Passenger : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? DocumentType { get; set; } // DNI, Pasaporte, etc.

    [MaxLength(50)]
    public string? DocumentNumber { get; set; }

    public DateTime? BirthDate { get; set; }

    [MaxLength(50)]
    public string? Nationality { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    // Gender for airline tickets
    [MaxLength(10)]
    public string? Gender { get; set; } // M, F

    // Additional notes
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
