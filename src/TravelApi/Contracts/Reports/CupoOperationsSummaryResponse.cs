namespace TravelApi.Contracts.Reports;

public record CupoOperationsSummaryResponse(
    int TotalCupos,
    int TotalCapacity,
    int TotalReserved,
    int TotalOverbookingLimit,
    int TotalAvailable,
    int TotalOverbooked
);
