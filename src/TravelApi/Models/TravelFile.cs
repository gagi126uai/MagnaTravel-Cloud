using System.ComponentModel.DataAnnotations;

namespace TravelApi.Models;

public class TravelFile
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string FileNumber { get; set; } = string.Empty; // e.g., "FILE-2024-001"
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty; // e.g., "Viaje Disney Familia Perez"
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Open"; // Open, Closed, Cancelled
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
    
    // Calculated properties can be added later (e.g., TotalMargin)
}
