namespace TravelApi.Models;

public class Tariff
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ProductType { get; set; } = "General";
    public Currency? Currency { get; set; }
    public decimal DefaultPrice { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TariffValidity> Validities { get; set; } = new List<TariffValidity>();
}
