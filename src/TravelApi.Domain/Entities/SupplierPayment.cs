using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Pago realizado a un proveedor (egreso)
/// </summary>
public class SupplierPayment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    // Optional links
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card, Check

    public string? Reference { get; set; } // Nro de transferencia, cheque, etc

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // B1.15 Fase 0' (CODE-10 / INV-2): soft-delete. Antes era hard-delete, lo
    // que perdia auditoria — un pago a proveedor borrado dejaba al CurrentBalance
    // restaurado pero sin registro de quien/cuando/por que. AuditLog en el
    // delete handler captura el motivo y el actor.
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }
}
