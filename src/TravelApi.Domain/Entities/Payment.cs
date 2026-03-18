using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Payment
{
    public int Id { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card
    public string? Reference { get; set; } // Transaction ID, Check #, etc.

    public string Status { get; set; } = "Paid"; // Paid, Pending, Cancelled

    public string? Notes { get; set; }

    // Soft Delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    // Direct link to Reserva (preferred)
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // Link via Servicio (for backwards compatibility)
    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }
}
