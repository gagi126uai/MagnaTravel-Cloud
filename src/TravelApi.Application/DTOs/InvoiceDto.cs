namespace TravelApi.Application.DTOs;

public class InvoiceDto
{
    public int Id { get; set; }

    public int TravelFileId { get; set; }
    public TravelFileDto? TravelFile { get; set; } // Navigation for frontend "File" and "Client" columns
    public int TipoComprobante { get; set; } 
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    public decimal ImporteTotal { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CAE { get; set; }
    public string? Resultado { get; set; }
    public string InvoiceType { get; set; } // Keep for convenience if needed
}
