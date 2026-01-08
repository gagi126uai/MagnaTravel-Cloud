using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record TariffSummaryDto(
    int Id,
    string Name,
    string ProductType,
    Currency? Currency,
    decimal DefaultPrice,
    bool IsActive,
    DateTime CreatedAt
);
