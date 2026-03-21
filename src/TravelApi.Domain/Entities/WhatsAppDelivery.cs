using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public static class WhatsAppDeliveryKinds
{
    public const string Voucher = "Voucher";
    public const string OperationalAck = "OperationalAck";
    public const string IncomingMessage = "IncomingMessage";
}

public static class WhatsAppDeliveryDirections
{
    public const string Outbound = "Outbound";
    public const string Inbound = "Inbound";
}

public static class WhatsAppDeliveryStatuses
{
    public const string PendingApproval = "PendingApproval";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string NeedsAgent = "NeedsAgent";
}

public class WhatsAppDelivery
{
    public int Id { get; set; }

    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Kind { get; set; } = WhatsAppDeliveryKinds.Voucher;

    [MaxLength(20)]
    public string Direction { get; set; } = WhatsAppDeliveryDirections.Outbound;

    [MaxLength(30)]
    public string Status { get; set; } = WhatsAppDeliveryStatuses.PendingApproval;

    [MaxLength(2000)]
    public string? MessageText { get; set; }

    [MaxLength(255)]
    public string? AttachmentName { get; set; }

    [MaxLength(200)]
    public string? BotMessageId { get; set; }

    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    [MaxLength(200)]
    public string? SentBy { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PreparedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
