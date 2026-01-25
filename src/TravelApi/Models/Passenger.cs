using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public class Passenger
{
    public int Id { get; set; }
    
    public int TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

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
