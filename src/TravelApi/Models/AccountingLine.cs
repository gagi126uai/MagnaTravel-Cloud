namespace TravelApi.Models;

public class AccountingLine
{
    public int Id { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = "USD";

    public int AccountingEntryId { get; set; }
    public AccountingEntry? AccountingEntry { get; set; }
}
