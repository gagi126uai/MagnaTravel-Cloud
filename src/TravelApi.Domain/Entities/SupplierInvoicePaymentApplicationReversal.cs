using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>Contra-fila inmutable que revierte una aplicación sin borrar su historia.</summary>
public class SupplierInvoicePaymentApplicationReversal : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int SupplierInvoicePaymentApplicationId { get; set; }
    public SupplierInvoicePaymentApplication Application { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Required, MaxLength(450)] public string CreatedByUserId { get; set; } = string.Empty;
    [MaxLength(200)] public string? CreatedByUserName { get; set; }
    [Required, MaxLength(500)] public string Reason { get; set; } = string.Empty;
}
