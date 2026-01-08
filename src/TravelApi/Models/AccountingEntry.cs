namespace TravelApi.Models;

public class AccountingEntry
{
    public int Id { get; set; }
    public DateTime EntryDate { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;

    public ICollection<AccountingLine> Lines { get; set; } = new List<AccountingLine>();
}
