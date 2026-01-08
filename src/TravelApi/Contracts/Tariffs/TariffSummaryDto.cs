using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record TariffSummaryDto(
    int Id,
    string Name,
    Currency Currency,
    decimal DefaultPrice,
    bool IsActive,
    DateTime CreatedAt
);
