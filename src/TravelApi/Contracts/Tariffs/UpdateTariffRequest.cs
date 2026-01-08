using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record UpdateTariffRequest(
    string Name,
    string? Description,
    Currency Currency,
    decimal DefaultPrice,
    bool IsActive
);
