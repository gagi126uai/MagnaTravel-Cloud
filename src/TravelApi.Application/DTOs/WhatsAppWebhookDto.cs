namespace TravelApi.Application.DTOs;

/// <summary>Payload del bot cuando captura un lead completo.</summary>
public class WhatsAppWebhookDto
{
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Interest { get; set; }
    public string? Dates { get; set; }
    public string? Travelers { get; set; }
    public string? Transcript { get; set; }
}

/// <summary>Payload del bot para mensajes individuales.</summary>
public class WhatsAppMessageDto
{
    public string Phone { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Sender { get; set; } = "Cliente"; // "Cliente" o "Agente"
}
