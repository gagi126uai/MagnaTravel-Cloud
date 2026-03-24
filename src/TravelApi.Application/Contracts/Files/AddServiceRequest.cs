namespace TravelApi.Application.Contracts.Files;

public record AddServiceRequest(
    string ServiceType,
    string? SupplierId,
    string? Description,
    string? ConfirmationNumber,
    DateTime DepartureDate,
    DateTime? ReturnDate,
    decimal SalePrice,
    decimal NetCost
);
