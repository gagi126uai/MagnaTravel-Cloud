namespace TravelApi.Models;

public static class ReservationStatuses
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";
    public const string Cancelled = "Cancelled";

    private static readonly IReadOnlyDictionary<string, int> StatusOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [Draft] = 0,
        [Confirmed] = 1,
        [Cancelled] = 2
    };

    public static bool IsValid(string status) => !string.IsNullOrWhiteSpace(status) && StatusOrder.ContainsKey(status);

    public static bool CanTransition(string currentStatus, string nextStatus)
    {
        if (!IsValid(currentStatus) || !IsValid(nextStatus))
        {
            return false;
        }

        if (string.Equals(currentStatus, nextStatus, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StatusOrder[nextStatus] == StatusOrder[currentStatus] + 1;
    }
}
