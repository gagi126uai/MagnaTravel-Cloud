namespace TravelApi.Application.DTOs;

/// <summary>
/// Payload que envía el bot de WhatsApp al webhook del ERP.
/// </summary>
public class WhatsAppWebhookDto
{
    /// <summary>Nombre completo del lead (capturado por el bot).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Número de teléfono con formato internacional (ej: +5491112345678).</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Destino o servicio de interés (ej: "Cancún", "Crucero").</summary>
    public string? Interest { get; set; }

    /// <summary>Transcripción completa de la conversación con el bot.</summary>
    public string? Transcript { get; set; }
}
