using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class PaymentReceiptStatuses
{
    public const string Issued = "Issued";
    public const string Voided = "Voided";
}

public class PaymentReceipt : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int ReservaId { get; set; }
    public Reserva Reserva { get; set; } = null!;

    [MaxLength(50)]
    public string ReceiptNumber { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = PaymentReceiptStatuses.Issued;

    public DateTime? VoidedAt { get; set; }

    /// <summary>
    /// B1.15 (2026-05-11): user que anulo el recibo. Audit trail fiscal — la
    /// fila Receipt se preserva indefinidamente para mantener numeracion correlativa
    /// (ARCA + Contable 2026-05-06). Null si nunca fue anulado.
    /// </summary>
    [MaxLength(64)]
    public string? VoidedByUserId { get; set; }

    [MaxLength(200)]
    public string? VoidedByUserName { get; set; }

    [MaxLength(500)]
    public string? VoidReason { get; set; }
}
