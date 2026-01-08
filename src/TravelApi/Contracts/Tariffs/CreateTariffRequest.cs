using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record CreateTariffRequest(
    string Name,
    string? Description,
    string ProductType,
    Currency? Currency,
    decimal DefaultPrice,
    bool IsActive
);
