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
    DateTime? PassportExpiry = null,
    // Semaforo de DNI vencido para cabotaje (2026-08-03): vencimiento del DNI. Mismo criterio que
    // PassportExpiry (opcional, al final, null = no informado). Ver Passenger.DocumentExpiry.
    DateTime? DocumentExpiry = null);

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
/// ADR-053 (2026-08-13, D3): edita la "fecha prometida" de la reserva — campo nuevo, separado y 100%
/// manual (patrón Odoo calculada+prometida). Reemplaza al viejo <c>UpdateReservaDatesRequest</c>: bajo la
/// decisión (1) del dueño, <c>StartDate</c>/<c>EndDate</c> pasaron a ser 100% calculados y de solo lectura
/// — ya no tiene sentido "corregirlos a mano". Ambos campos son opcionales: enviar null deja la fecha
/// prometida sin cambios; enviar una fecha la setea; para "borrar" una fecha prometida pasar
/// <c>ClearPromisedStartDate</c> / <c>ClearPromisedEndDate</c>.
/// </summary>
public record UpdatePromisedDatesRequest(
    DateTime? PromisedStartDate,
    DateTime? PromisedEndDate,
    bool ClearPromisedStartDate = false,
    bool ClearPromisedEndDate = false);

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
/// Obra "PDF de presupuesto" (decisión #2 firmada del dueño, 2026-08-11/12): texto de "Formas de pago"
/// propio de UN presupuesto puntual (cuotas, señas, medios aceptados). <see cref="Text"/> null/vacío
/// BORRA el texto propio (el PDF cae a la plantilla de Configuración de la agencia, o a nada si tampoco
/// hay plantilla) — mismo criterio de reemplazo total que <c>UpdateBudgetConditionBlockRequest</c>
/// (ReportsController), no el anti-clobber "null = no tocar" de campos dentro de un request con MUCHOS
/// campos: acá el único propósito del endpoint es este texto, así que null es una instrucción explícita
/// de "dejalo vacío", no "no tocar nada".
/// </summary>
public record UpdateBudgetPaymentTermsRequest(string? Text);

/// <summary>
/// Obra "PDF ronda 2" (decisión firmada del dueño, 2026-08-14, spec §6): UNA fila del plan de pagos del
/// TOTAL del presupuesto ("Al confirmar la reserva — 500 USD"). <see cref="DueText"/> es el texto de
/// cuándo se paga, tal como lo escribe el vendedor (no una fecha estructurada: puede ser "Al confirmar
/// la reserva" o "10 de enero de 2027", texto libre).
/// </summary>
public record PaymentPlanInstallmentRequest(string DueText, decimal Amount, string? Currency);

/// <summary>
/// Body de <c>PUT /api/reservas/{id}/budget-payment-plan</c>: REEMPLAZA la lista COMPLETA del plan de
/// pagos del presupuesto, en el orden en que llegan (posiciones 1..N) — mismo criterio de "reemplazo
/// total" que <see cref="UpdateBudgetPaymentTermsRequest"/>, no un anti-clobber campo-por-campo. Lista
/// vacía/null BORRA el plan entero (el bloque deja de aparecer en el PDF, regla espejo de la obra: sin
/// filas cargadas no hay nada que mostrar).
/// </summary>
public record UpdatePaymentPlanRequest(IReadOnlyList<PaymentPlanInstallmentRequest>? Installments);

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
