namespace TravelApi.Application.Contracts.Reservations;

public record PassengerUpsertRequest(
    string FullName,
    string? DocumentType,
    string? DocumentNumber,
    DateTime? BirthDate,
    string? Nationality,
    string? Phone,
    string? Email,
    string? Gender,
    string? Notes);

public record ReservationPaymentUpsertRequest(
    decimal Amount,
    DateTime PaidAt,
    string Method,
    string? Reference,
    string? Notes);
