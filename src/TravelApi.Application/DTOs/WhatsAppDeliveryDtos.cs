namespace TravelApi.Application.DTOs;

public class UpdateReservaWhatsAppContactRequest
{
    public string? WhatsAppPhoneOverride { get; set; }
}

public class SendVoucherRequest
{
    public string? Caption { get; set; }
}

public class WhatsAppVoucherPreviewResponse
{
    public bool CanSend { get; set; }
    public string? ResolvedPhone { get; set; }
    public string PhoneSource { get; set; } = "none";
    public string? PhoneOverride { get; set; }
    public string Caption { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public class WhatsAppDeliveryDto
{
    public int Id { get; set; }
    public int ReservaId { get; set; }
    public int? CustomerId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? MessageText { get; set; }
    public string? AttachmentName { get; set; }
    public string? BotMessageId { get; set; }
    public string? CreatedBy { get; set; }
    public string? SentBy { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PreparedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
