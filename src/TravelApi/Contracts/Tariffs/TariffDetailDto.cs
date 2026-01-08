using TravelApi.Models;

namespace TravelApi.Contracts.Tariffs;

public record TariffDetailDto(
    int Id,
    string Name,
    string? Description,
    string ProductType,
    Currency? Currency,
    decimal DefaultPrice,
    bool IsActive,
    DateTime CreatedAt,
    IReadOnlyCollection<TariffValidityDto> Validities
);
