using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

/// <summary>
/// Pago realizado a un proveedor (egreso)
/// </summary>
public class SupplierPayment
{
    public int Id { get; set; }

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    // Optional links
    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

    public int? ReservationId { get; set; }
    public Reservation? Reservation { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card, Check

    public string? Reference { get; set; } // Nro de transferencia, cheque, etc

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
