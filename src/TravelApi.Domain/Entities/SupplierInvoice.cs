using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>Documento comercial recibido del operador. No es comprobante fiscal AFIP y no duplica deuda.</summary>
public class SupplierInvoice : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    [Required, MaxLength(80)] public string Number { get; set; } = string.Empty;
    [Required, MaxLength(3)] public string Currency { get; set; } = Monedas.ARS;
    public DateTime IssuedAt { get; set; }
    public DateTime DueDate { get; set; }
    public SupplierInvoiceStatus Status { get; set; } = SupplierInvoiceStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Required, MaxLength(450)] public string CreatedByUserId { get; set; } = string.Empty;
    [MaxLength(200)] public string? CreatedByUserName { get; set; }
    public DateTime? VoidedAt { get; set; }
    [MaxLength(500)] public string? VoidReason { get; set; }
    public ICollection<SupplierInvoiceLine> Lines { get; set; } = new List<SupplierInvoiceLine>();
    public ICollection<SupplierInvoicePaymentApplication> PaymentApplications { get; set; } = new List<SupplierInvoicePaymentApplication>();
}

public enum SupplierInvoiceStatus { Open = 0, PartiallyPaid = 1, Paid = 2, Void = 3 }
