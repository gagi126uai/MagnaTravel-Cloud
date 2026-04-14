namespace TravelApi.Application.DTOs;

public class TransferBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string PickupLocation { get; set; } = string.Empty;
    public string DropoffLocation { get; set; } = string.Empty;
    public DateTime PickupDateTime { get; set; }
    public string VehicleType { get; set; } = "Private";
    public int Passengers { get; set; } = 1;
    public bool IsRoundTrip { get; set; }
    public DateTime? ReturnDateTime { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    public bool IsPriceSynced { get; set; } = true;
}
