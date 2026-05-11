namespace TravelApi.Application.DTOs;

public class InvoiceListDto
{
    public Guid PublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? CustomerName { get; set; }
    public int TipoComprobante { get; set; }
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    public decimal ImporteTotal { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CAE { get; set; }
    public string? Resultado { get; set; }
    public string? Observaciones { get; set; }
    public bool WasForced { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
    public DateTime? ForcedAt { get; set; }
    public decimal OutstandingBalanceAtIssuance { get; set; }
    public string InvoiceType { get; set; } = string.Empty;

    // B1.15 (2026-05-11): para UI consistente con InvoiceDto.
    public string AnnulmentStatus { get; set; } = "None";
    public Guid? OriginalInvoicePublicId { get; set; }
    public long? OriginalInvoiceNumeroComprobante { get; set; }
    public int? OriginalInvoiceTipoComprobante { get; set; }
    public int? OriginalInvoicePuntoDeVenta { get; set; }
}
