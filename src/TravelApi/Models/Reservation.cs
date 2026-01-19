namespace TravelApi.Models;

public class Reservation
{
    public int Id { get; set; }
    public string ReferenceCode { get; set; } = string.Empty;
    public string Status { get; set; } = ReservationStatuses.Draft;
    public string ProductType { get; set; } = "Flight";
    public DateTime DepartureDate { get; set; }
    public DateTime? ReturnDate { get; set; }
    public decimal BasePrice { get; set; }
    public decimal Commission { get; set; }
    public decimal TotalAmount { get; set; }
    public string? SupplierName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }
}
