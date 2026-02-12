namespace TravelApi.DTOs;

public class TravelFileDto
{
    public int Id { get; set; }
    public int FileNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "Presupuesto";
    public int? CustomerId { get; set; }
    public string? CustomerName { get; set; } // Flattened
    public decimal TotalCost { get; set; }
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Collections
    public List<PassengerDto> Passengers { get; set; } = new();
    public List<FlightSegmentDto> FlightSegments { get; set; } = new();
    public List<HotelBookingDto> HotelBookings { get; set; } = new();
    public List<TransferBookingDto> TransferBookings { get; set; } = new();
    public List<PackageBookingDto> PackageBookings { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();    
}
