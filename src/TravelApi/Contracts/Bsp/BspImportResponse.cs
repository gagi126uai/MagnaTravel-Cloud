namespace TravelApi.Contracts.Bsp;

public record BspImportResponse(
    int BatchId,
    string Status,
    int RawCount,
    int NormalizedCount,
    int ReconciledCount);
