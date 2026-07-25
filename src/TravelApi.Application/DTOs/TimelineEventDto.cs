namespace TravelApi.Application.DTOs;

public class TimelineEventDto
{
    public DateTime Timestamp { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityPublicId { get; set; }

    // Campo aditivo (barrido T5, 2026-07-24, item #6): monto/moneda/metodo del COBRO, SOLO cuando el
    // evento es sobre un Pago (RelatedEntityType == "Payment"). Se llenan leyendo el Payment real, NO
    // desde el diff generico de auditoria (Details): ese diff falla en silencio para los eventos de
    // ALTA (ver el comentario en TimelineService.GetTimelineAsync) y termina mostrando el texto
    // generico "Modificaciones en campos técnicos.", perdiendo el monto y el metodo. Con estos tres
    // campos separados, la pantalla de historial puede armar su propio texto ("$ 1.500 por
    // Transferencia") sin depender de que el diff generico funcione.
    //
    // PaymentMethod viaja TAL CUAL esta en la base ("Cash" / "Transfer" / "Card", ver Payment.Method):
    // el texto final en español lo arma el frontend, no el backend (mismo criterio que el resto de los
    // enums-string del dominio, p.ej. Reserva.Status).
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? PaymentMethod { get; set; }
}
