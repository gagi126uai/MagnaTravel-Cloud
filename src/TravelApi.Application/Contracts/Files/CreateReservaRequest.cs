namespace TravelApi.Application.Contracts.Files;

public class CreateReservaRequest
{
    public string Name { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public DateTime? StartDate { get; set; }
    public string? Description { get; set; }
    // ADR-020 (2026-06-07): el campo Status se ELIMINO. Toda reserva nace en Cotizacion
    // (INV-020-01); el estado inicial ya no se puede elegir desde el request. Si el frontend
    // envia "status" en el body, se ignora silenciosamente (props JSON extra no rompen el binding).
}
