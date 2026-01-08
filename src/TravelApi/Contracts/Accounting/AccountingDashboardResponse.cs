namespace TravelApi.Contracts.Accounting;

public record AccountingDashboardResponse(
    int TotalEntries,
    int TotalLines,
    decimal TotalDebits,
    decimal TotalCredits);
