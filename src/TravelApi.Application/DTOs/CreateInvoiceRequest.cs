namespace TravelApi.Application.DTOs;

public class CreateInvoiceRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public int CbteTipo { get; set; } // Optional: To override automatic detection
    public int Concepto { get; set; } = 3; // 1: Productos, 2: Servicios, 3: Ambos
    public int DocTipo { get; set; } = 99;
    public long DocNro { get; set; } = 0;
    
    public List<InvoiceItemDto> Items { get; set; } = new();
    public List<InvoiceTributeDto> Tributes { get; set; } = new();
    public string? OriginalInvoiceId { get; set; } 
    public bool IsCreditNote { get; set; } 
    public bool IsDebitNote { get; set; } 
    public bool ForceIssue { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
}
