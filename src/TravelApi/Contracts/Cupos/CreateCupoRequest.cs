namespace TravelApi.Contracts.Cupos;

public record CreateCupoRequest(
    string Name,
    string ProductType,
    DateTime TravelDate,
    int Capacity,
    int OverbookingLimit
);
