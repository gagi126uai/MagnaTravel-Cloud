using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class CashMovementDirections
{
    public const string Income = "Income";
    public const string Expense = "Expense";
}

public class ManualCashMovement : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [MaxLength(20)]
    public string Direction { get; set; } = CashMovementDirections.Expense;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string Method { get; set; } = "Transfer";

    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Reference { get; set; }

    [MaxLength(200)]
    public string CreatedBy { get; set; } = "System";

    public bool IsVoided { get; set; }
    public DateTime? VoidedAt { get; set; }

    public int? RelatedReservaId { get; set; }
    public Reserva? RelatedReserva { get; set; }

    public int? RelatedSupplierId { get; set; }
    public Supplier? RelatedSupplier { get; set; }
}
