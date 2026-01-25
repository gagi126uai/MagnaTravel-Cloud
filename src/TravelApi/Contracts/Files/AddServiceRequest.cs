namespace TravelApi.Contracts.Files;

public record AddServiceRequest(
    string ServiceType,
    int? SupplierId,
    string? Description,
    string? ConfirmationNumber,
    DateTime DepartureDate,
    DateTime? ReturnDate,
    decimal SalePrice,
    decimal NetCost
);
