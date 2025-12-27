namespace TravelApi.Models;

public class Payment
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
    public string Method { get; set; } = "Card";

    public int ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
}
