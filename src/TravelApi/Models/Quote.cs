namespace TravelApi.Models;

public class Quote
{
    public int Id { get; set; }
    public string ReferenceCode { get; set; } = string.Empty;
    public string Status { get; set; } = QuoteStatuses.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public ICollection<QuoteVersion> Versions { get; set; } = new List<QuoteVersion>();
}
