namespace TravelApi.DTOs;

public class FlightSegmentDto
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string AirlineCode { get; set; } = string.Empty;
    public string? AirlineName { get; set; }
    public string FlightNumber { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime DepartureTime { get; set; }
    public DateTime ArrivalTime { get; set; }
    public string CabinClass { get; set; } = "Economy";
    public string Status { get; set; } = "HK";
    public string? PNR { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
}
