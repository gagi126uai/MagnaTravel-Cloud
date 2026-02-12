namespace TravelApi.DTOs;

public class TransferBookingDto
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string PickupLocation { get; set; } = string.Empty;
    public string DropoffLocation { get; set; } = string.Empty;
    public DateTime PickupDateTime { get; set; }
    public string VehicleType { get; set; } = "Sedan";
    public int Passengers { get; set; }
    public bool IsRoundTrip { get; set; }
    public DateTime? ReturnDateTime { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = "Solicitado";
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
}
