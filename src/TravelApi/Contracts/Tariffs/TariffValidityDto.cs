namespace TravelApi.Contracts.Tariffs;

public record TariffValidityDto(
    int Id,
    DateTime StartDate,
    DateTime EndDate,
    decimal Price,
    bool IsActive,
    string? Notes
);
