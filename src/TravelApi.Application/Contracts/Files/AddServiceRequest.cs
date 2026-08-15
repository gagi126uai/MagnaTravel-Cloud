namespace TravelApi.Application.Contracts.Files;

public record AddServiceRequest(
    string ServiceType,
    string? SupplierId,
    string? Description,
    string? ConfirmationNumber,
    DateTime DepartureDate,
    DateTime? ReturnDate,
    decimal SalePrice,
    decimal NetCost,
    string? RateId = null,
    // ADR-026 (vencimientos, auditoria 2026-06-12): fecha limite de pago al operador del
    // servicio generico. Los tipos catalogados ya lo reciben por su propio request; el
    // generico se cargaba/editaba solo por este record y le faltaba el campo -> su alarma
    // de pago al operador nunca disparaba. Opcional; null = sin fecha.
    DateTime? OperatorPaymentDeadline = null,
    // Semaforo de DNI vencido para cabotaje (2026-08-03): ambito geografico del servicio, como texto
    // legible ("Nacional"/"Internacional"). Opcional; null o un texto no reconocido = no se toca
    // (validacion SUAVE, ver ServiceGeographicScopeText.ParseOrNull).
    string? GeographicScope = null,
    // Obra "PDF ronda 2" (2026-08-14): plan de cuotas informativo del servicio generico. Mapeo DIRECTO
    // (null = borrar), igual que el resto de los campos de este record — este UNICO request sirve tanto
    // para alta como para edicion, y la ficha inline siempre reenvia los 2 casilleros. Ver
    // ServicioReserva.InstallmentsCount/InstallmentAmount.
    int? InstallmentsCount = null,
    decimal? InstallmentAmount = null
);
