namespace TravelApi.Application.DTOs;

public class InvoiceItemDto
{
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public int AlicuotaIvaId { get; set; } // 3=0%, 4=10.5%, 5=21%, etc.
}
