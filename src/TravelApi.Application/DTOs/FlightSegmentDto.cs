namespace TravelApi.Application.DTOs;

public class FlightSegmentDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
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
    public decimal NetCost { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Flight";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
