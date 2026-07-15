using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>Imputación explícita de un pago ya existente. Concilia; no vuelve a mover caja.</summary>
public class SupplierInvoicePaymentApplication : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int SupplierInvoiceId { get; set; }
    public SupplierInvoice SupplierInvoice { get; set; } = null!;
    public int SupplierPaymentId { get; set; }
    public SupplierPayment SupplierPayment { get; set; } = null!;
    public decimal Amount { get; set; }
    public bool IsReversed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Required, MaxLength(450)] public string CreatedByUserId { get; set; } = string.Empty;
    [MaxLength(200)] public string? CreatedByUserName { get; set; }
    public SupplierInvoicePaymentApplicationReversal? Reversal { get; set; }
}
