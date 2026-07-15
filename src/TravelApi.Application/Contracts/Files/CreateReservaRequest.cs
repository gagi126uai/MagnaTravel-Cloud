namespace TravelApi.Application.Contracts.Files;

public class CreateReservaRequest
{
    public string Name { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public DateTime? StartDate { get; set; }
    public string? Description { get; set; }
    // Decision de producto 2026-07-15: el campo Status sigue fuera del contrato y toda reserva
    // nueva nace en Presupuesto. Si el frontend
    // envia "status" en el body, se ignora silenciosamente (props JSON extra no rompen el binding).

    // CRM leads (2026-06-12): si la reserva nace de un lead (boton "Crear presupuesto desde lead"),
    // el front manda aca el PublicId del lead de origen. Cierra el circuito: linkea la reserva al
    // lead (Reserva.SourceLeadId) y marca el lead como Ganado. Es OPCIONAL — una reserva creada a
    // mano, sin pasar por un lead, deja este campo en null y no cambia ningun comportamiento.
    public string? SourceLeadPublicId { get; set; }
}
