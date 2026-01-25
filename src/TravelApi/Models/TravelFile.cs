using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public static class FileStatus
{
    public const string Budget = "Presupuesto";
    public const string Reserved = "Reservado";
    public const string Operational = "Operativo";
    public const string Closed = "Cerrado";
    public const string Cancelled = "Cancelado";
}

public class TravelFile
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string FileNumber { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = FileStatus.Budget;
    
    // Dates
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Payer/Main Client
    public int? PayerId { get; set; }
    public Customer? Payer { get; set; }

    // Financials
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSale { get; set; } = 0;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; } = 0;

    // Navigation
    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
    public ICollection<Passenger> Passengers { get; set; } = new List<Passenger>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
