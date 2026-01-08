namespace TravelApi.Models;

public static class QuoteStatuses
{
    public const string Draft = "Draft";
    public const string Sent = "Sent";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft,
        Sent,
        Approved,
        Rejected
    };

    public static bool IsValid(string status) => !string.IsNullOrWhiteSpace(status) && All.Contains(status);
}
