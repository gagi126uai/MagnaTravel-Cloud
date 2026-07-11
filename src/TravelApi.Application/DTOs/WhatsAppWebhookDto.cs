using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

/// <summary>Payload del bot cuando captura un lead completo.</summary>
public class WhatsAppWebhookDto
{
    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;
    [Required, MaxLength(32)]
    public string Phone { get; set; } = string.Empty;
    [MaxLength(500)]
    public string? Interest { get; set; }
    [MaxLength(250)]
    public string? Dates { get; set; }
    [MaxLength(100)]
    public string? Travelers { get; set; }
    [MaxLength(10_000)]
    public string? Transcript { get; set; }
}

/// <summary>Payload del bot para mensajes individuales.</summary>
public class WhatsAppMessageDto
{
    [Required, MaxLength(32)]
    public string Phone { get; set; } = string.Empty;
    [Required, MaxLength(2_000)]
    public string Message { get; set; } = string.Empty;
    [Required, MaxLength(50)]
    public string Sender { get; set; } = "Cliente"; // "Cliente" o "Agente"
    public bool SkipLeadAutoCreation { get; set; }
}
