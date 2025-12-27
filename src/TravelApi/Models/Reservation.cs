namespace TravelApi.Models;

public class Reservation
{
    public int Id { get; set; }
    public string ReferenceCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public DateTime TravelDate { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
