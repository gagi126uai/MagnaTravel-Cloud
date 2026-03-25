namespace TravelApi.Domain.Entities;

public class BusinessSequence
{
    public int Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int Year { get; set; }
    public long LastValue { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
