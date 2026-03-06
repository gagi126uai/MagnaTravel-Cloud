namespace TravelApi.Application.DTOs;

public class InvoiceTributeDto
{
    public int TributeId { get; set; } // 99=IIBB, etc.
    public string Description { get; set; } = string.Empty;
    public decimal BaseImponible { get; set; }
    public decimal Alicuota { get; set; }
    public decimal Importe { get; set; }
}
