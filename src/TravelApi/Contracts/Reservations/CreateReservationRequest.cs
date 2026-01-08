namespace TravelApi.Contracts.Reservations;

public record CreateReservationRequest(
    string ReferenceCode,
    string ProductType,
    DateTime DepartureDate,
    DateTime? ReturnDate,
    decimal BasePrice,
    decimal Commission,
    decimal TotalAmount,
    string? SupplierName,
    int CustomerId
);
