using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class PaymentEntryTypes
{
    public const string Payment = "Payment";
    public const string CreditNoteReversal = "CreditNoteReversal";
}

public class Payment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card
    public string? Reference { get; set; } // Transaction ID, Check #, etc.

    public string Status { get; set; } = "Paid"; // Paid, Pending, Cancelled

    public string? Notes { get; set; }

    public string EntryType { get; set; } = PaymentEntryTypes.Payment;
    public bool AffectsCash { get; set; } = true;

    // B1.15 Fase 1: trazabilidad de quien y cuando se registro el pago.
    // CreatedAt es NOT NULL (default DateTime.UtcNow / CURRENT_TIMESTAMP).
    // CreatedBy* es nullable para soportar backfill de historicos.
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Soft Delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    // Direct link to Reserva (preferred)
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // Link via Servicio (for backwards compatibility)
    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }

    public int? RelatedInvoiceId { get; set; }
    public Invoice? RelatedInvoice { get; set; }

    public int? OriginalPaymentId { get; set; }
    public Payment? OriginalPayment { get; set; }
    public ICollection<Payment> Reversals { get; set; } = new List<Payment>();

    public PaymentReceipt? Receipt { get; set; }
}
