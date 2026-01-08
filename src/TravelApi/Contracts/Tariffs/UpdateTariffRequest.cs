using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record UpdateTariffRequest(
    string Name,
    string? Description,
    string ProductType,
    Currency? Currency,
    decimal DefaultPrice,
    bool IsActive
);
