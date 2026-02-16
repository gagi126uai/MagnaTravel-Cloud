namespace TravelApi.DTOs;

public class InvoiceDto
{
    public int Id { get; set; }
    public int TipoComprobante { get; set; } // Raw AFIP Code (1, 6, 11, etc.)
    public string InvoiceType { get; set; } = "C";
    public int PointOfSale { get; set; }
    public long InvoiceNumber { get; set; }
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public string? CAE { get; set; }
    public string? Status { get; set; }
}
