namespace TravelApi.Application.DTOs;

public class PackageBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = "Solicitado";
    public bool IncludesHotel { get; set; }
    public bool IncludesFlight { get; set; }
    public bool IncludesTransfer { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
}
