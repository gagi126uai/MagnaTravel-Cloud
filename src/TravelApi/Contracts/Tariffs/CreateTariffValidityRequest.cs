namespace TravelApi.Contracts.Tariffs;

public record CreateTariffValidityRequest(
    DateTime StartDate,
    DateTime EndDate,
    decimal Price,
    bool IsActive,
    string? Notes
);
