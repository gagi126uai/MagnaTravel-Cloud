namespace TravelApi.Application.DTOs;

public class HotelBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    public string RoomType { get; set; } = "Doble";
    public string? MealPlan { get; set; }
    public int Rooms { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = "Solicitado";
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
}
