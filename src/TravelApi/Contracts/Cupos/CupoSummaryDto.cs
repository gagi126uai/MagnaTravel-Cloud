namespace TravelApi.Contracts.Cupos;

public record CupoSummaryDto(
    int Id,
    string Name,
    string ProductType,
    DateTime TravelDate,
    int Capacity,
    int Reserved,
    int OverbookingLimit,
    int Available,
    Guid RowVersion
);
