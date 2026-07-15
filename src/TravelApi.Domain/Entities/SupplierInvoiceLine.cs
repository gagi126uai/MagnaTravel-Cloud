using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class SupplierInvoiceLine : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int SupplierInvoiceId { get; set; }
    public SupplierInvoice SupplierInvoice { get; set; } = null!;
    public int ReservaId { get; set; }
    public Reserva Reserva { get; set; } = null!;
    [Required, MaxLength(20)] public string ServiceRecordKind { get; set; } = string.Empty;
    public Guid ServicePublicId { get; set; }
    [Required, MaxLength(200)] public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
