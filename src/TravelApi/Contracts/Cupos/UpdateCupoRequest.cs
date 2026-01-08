namespace TravelApi.Contracts.Cupos;

public record UpdateCupoRequest(
    string Name,
    string ProductType,
    DateTime TravelDate,
    int Capacity,
    int OverbookingLimit,
    Guid RowVersion
);
