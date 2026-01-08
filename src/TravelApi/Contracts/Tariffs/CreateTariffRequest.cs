using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record CreateTariffRequest(
    string Name,
    string? Description,
    Currency Currency,
    decimal DefaultPrice,
    bool IsActive
);
