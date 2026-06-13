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
    DateTime? OperatorPaymentDeadline = null
);
