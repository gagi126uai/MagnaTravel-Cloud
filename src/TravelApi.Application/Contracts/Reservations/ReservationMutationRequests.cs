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
/// Editar manualmente las fechas de salida/regreso de la reserva. Se usa para casos
/// donde las fechas no se pueden derivar de los servicios (servicios sin fecha clara,
/// reservas viejas con StartDate/EndDate=null, o overrides explicitos del operador).
/// Ambos campos son opcionales: enviar null deja la fecha sin cambios; enviar una
/// fecha la setea; para "borrar" una fecha pasar `clearStartDate` / `clearEndDate`.
/// </summary>
public record UpdateReservaDatesRequest(
    DateTime? StartDate,
    DateTime? EndDate,
    bool ClearStartDate = false,
    bool ClearEndDate = false);

/// <summary>
/// Request para cambiar el Status de un servicio (Hotel/Transfer/Package/Flight/
/// ServicioReserva) y opcionalmente el codigo de confirmacion del proveedor.
/// Usado desde la cuenta corriente del proveedor para permitir al operador
/// confirmar servicios sin entrar a cada reserva. Para Flight el confirmation
/// se almacena en el campo PNR; para el resto en ConfirmationNumber.
/// </summary>
public record ServiceStatusUpdateRequest(string Status, string? ConfirmationNumber = null);
