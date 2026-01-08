namespace TravelApi.Contracts.Bsp;

public record BspBatchSummaryResponse(
    int BatchId,
    string FileName,
    string Format,
    string Status,
    DateTime ImportedAt,
    DateTime? ClosedAt,
    int RawCount,
    int NormalizedCount,
    int MatchedCount,
    int MismatchCount,
    int MissingCount,
    decimal TotalImportedAmount,
    decimal TotalMatchedAmount);
