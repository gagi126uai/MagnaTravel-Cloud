namespace TravelApi.Contracts.Tariffs;

public record UpdateTariffValidityRequest(
    DateTime StartDate,
    DateTime EndDate,
    decimal Price,
    bool IsActive,
    string? Notes
);
