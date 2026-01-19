namespace TravelApi.Models;

public class Customer
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Retail Pivot: Financials
    public decimal CreditLimit { get; set; } = 0;
    public decimal CurrentBalance { get; set; } = 0; // Positive = they owe us

    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
}
