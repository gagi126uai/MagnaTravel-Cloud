namespace TravelApi.Application.DTOs;

public class InvoiceDto
{
    public Guid PublicId { get; set; }

    public Guid? ReservaPublicId { get; set; }
    public ReservaDto? Reserva { get; set; } // Navigation for frontend "Reserva" and "Client" columns
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
    public string InvoiceType { get; set; } = string.Empty; // Keep for convenience if needed
}
