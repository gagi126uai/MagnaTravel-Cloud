using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class ServicioReservaDto
{
    public Guid PublicId { get; set; }
    public string? ServiceType { get; set; }
    public string? ProductType { get; set; }
    public string? Description { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = ReservationStatuses.Draft;
    
    public DateTime DepartureDate { get; set; }
    public DateTime? ReturnDate { get; set; }
    
    public decimal NetCost { get; set; }
    public decimal SalePrice { get; set; }
    public decimal Commission { get; set; }
    public decimal Tax { get; set; }
    
    public string? SupplierName { get; set; }
    public Guid? SupplierPublicId { get; set; }
    
    public Guid? RatePublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
}
