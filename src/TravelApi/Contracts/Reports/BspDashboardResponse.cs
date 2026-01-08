namespace TravelApi.Contracts.Reports;

public record BspDashboardResponse(
    int TotalBatches,
    int OpenBatches,
    int ClosedBatches,
    int TotalRecords,
    decimal TotalImportedAmount,
    decimal TotalMatchedAmount);
