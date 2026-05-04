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

public record PassengerCountsRequest(
    int AdultCount,
    int ChildCount,
    int InfantCount);

/// <summary>
/// Request para cambiar SOLO el Status de un servicio (Hotel/Transfer/Package/Flight/
/// ServicioReserva). Usado desde la cuenta corriente del proveedor para permitir al
/// operador confirmar servicios sin entrar a cada reserva.
/// </summary>
public record ServiceStatusUpdateRequest(string Status);
