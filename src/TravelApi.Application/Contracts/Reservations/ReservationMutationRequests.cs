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
/// Request para cambiar el Status de un servicio (Hotel/Transfer/Package/Flight/
/// ServicioReserva) y opcionalmente el codigo de confirmacion del proveedor.
/// Usado desde la cuenta corriente del proveedor para permitir al operador
/// confirmar servicios sin entrar a cada reserva. Para Flight el confirmation
/// se almacena en el campo PNR; para el resto en ConfirmationNumber.
/// </summary>
public record ServiceStatusUpdateRequest(string Status, string? ConfirmationNumber = null);
