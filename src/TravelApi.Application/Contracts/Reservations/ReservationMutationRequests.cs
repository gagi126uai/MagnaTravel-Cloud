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
    string? Notes,
    // Auditoria ERP 2026-06-12 (item 8): vencimiento del pasaporte. Opcional al final para no romper
    // los callers posicionales existentes (null = no informado). Ver Passenger.PassportExpiry.
    DateTime? PassportExpiry = null);

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
/// REPROGRAMAR VIAJE (2026-06-23): mueve TODAS las fechas de TODOS los servicios de una reserva
/// JUNTAS, conservando la duracion y la separacion entre ellas. Es el equivalente a "el operador
/// corrio el viaje N dias" sin tener que editar servicio por servicio.
///
/// <para>Modos de uso (se elige UNO):</para>
/// <list type="bullet">
///   <item><b>Por desplazamiento</b> (modo principal): <see cref="DaysShift"/> = cuantos dias mover
///     (+ adelanta, - atrasa). Ej: +7 = todo el viaje una semana mas tarde.</item>
///   <item><b>Por nueva fecha de salida</b> (opcional): <see cref="NewStartDate"/> = la fecha de salida
///     deseada. El service deriva el shift = (NewStartDate - StartDate actual de la reserva), en dias
///     enteros, y lo aplica igual que el modo por desplazamiento. Requiere que la reserva ya tenga
///     un StartDate (servicios con fecha); si no, no hay desde donde derivar y se rechaza.</item>
/// </list>
///
/// <para>Solo se permite enviar uno de los dos. <see cref="DaysShift"/> = 0 (y sin NewStartDate) es un
/// no-op valido (no mueve nada). NO toca precios, costos ni comisiones: las fechas no entran en la plata.</para>
/// </summary>
public record RescheduleReservaRequest(
    int? DaysShift = null,
    DateTime? NewStartDate = null);

/// <summary>
/// Request para cambiar el Status de un servicio (Hotel/Transfer/Package/Flight/
/// ServicioReserva) y opcionalmente el codigo de confirmacion del proveedor.
/// Usado desde la cuenta corriente del proveedor para permitir al operador
/// confirmar servicios sin entrar a cada reserva. Para Flight el confirmation
/// se almacena en el campo PNR; para el resto en ConfirmationNumber.
/// </summary>
public record ServiceStatusUpdateRequest(string Status, string? ConfirmationNumber = null);

/// <summary>
/// Body de <c>POST /api/reservas/{id}/annul-with-credit</c> (caso (3): anular en firme sin factura, con cobros,
/// convirtiendo la plata en saldo a favor). <see cref="Reason"/> es el MOTIVO obligatorio de la anulacion
/// declarado por el operador (min 10 chars; mismo criterio que el draft de cancelacion con NC). Como mueve plata
/// a saldo a favor, la justificacion queda registrada en la auditoria. El controller y el service validan el
/// largo server-side (no se confia en el front). Nullable para controlar nosotros el mensaje de error en español
/// en vez del 400 generico de model-binding cuando el body llega vacio.
/// </summary>
public record AnnulWithCreditRequest(string? Reason);
