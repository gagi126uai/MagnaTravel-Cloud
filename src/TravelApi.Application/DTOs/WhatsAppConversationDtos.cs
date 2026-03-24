namespace TravelApi.Application.DTOs;

public class WhatsAppConversationListItemDto
{
    public string ConversationType { get; set; } = string.Empty;
    public Guid EntityPublicId { get; set; }
    public Guid? LeadPublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? StatusLabel { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime LastMessageAt { get; set; }
    public bool NeedsAttention { get; set; }
    public int MessageCount { get; set; }
}

public class WhatsAppConversationDetailDto
{
    public string ConversationType { get; set; } = string.Empty;
    public Guid EntityPublicId { get; set; }
    public Guid? LeadPublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? StatusLabel { get; set; }
    public string? InterestedIn { get; set; }
    public string? TravelDates { get; set; }
    public string? Travelers { get; set; }
    public IReadOnlyList<WhatsAppConversationMessageDto> Messages { get; set; } = Array.Empty<WhatsAppConversationMessageDto>();
}

public class WhatsAppConversationMessageDto
{
    public string Id { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string SenderLabel { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? Kind { get; set; }
}
