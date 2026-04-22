namespace TravelApi.Application.DTOs;

public class WhatsAppLeadWebhookResult
{
    public bool Created { get; set; }
    public Guid LeadPublicId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
}

public class WhatsAppIncomingMessageResult
{
    public string HandledBy { get; set; } = string.Empty;
    public Guid? LeadPublicId { get; set; }
    public bool AutoCreated { get; set; }
    public bool AllowBotCapture { get; set; }
}
