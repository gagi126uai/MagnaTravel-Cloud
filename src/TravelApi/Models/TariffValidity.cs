namespace TravelApi.Models;

public class TariffValidity
{
    public int Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int TariffId { get; set; }
    public Tariff? Tariff { get; set; }

    public bool HasValidRange() => EndDate >= StartDate;

    public bool IsActiveOn(DateTime date)
    {
        var checkDate = date.Date;
        return IsActive && checkDate >= StartDate.Date && checkDate <= EndDate.Date;
    }
}
