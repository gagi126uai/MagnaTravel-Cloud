using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public static class MessageDeliveryKinds
{
    public const string Text = "Text";
    public const string Voucher = "Voucher";
    public const string IncomingMessage = "IncomingMessage";
    public const string OperationalAck = "OperationalAck";
}

public static class MessageDeliveryChannels
{
    public const string WhatsApp = "WhatsApp";
}

public static class MessageDeliveryStatuses
{
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string NeedsAgent = "NeedsAgent";
}

public class MessageDelivery : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? PassengerId { get; set; }
    public Passenger? Passenger { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? VoucherId { get; set; }
    public Voucher? Voucher { get; set; }

    [MaxLength(30)]
    public string Channel { get; set; } = MessageDeliveryChannels.WhatsApp;

    [MaxLength(30)]
    public string Kind { get; set; } = MessageDeliveryKinds.Text;

    [MaxLength(30)]
    public string Status { get; set; } = MessageDeliveryStatuses.Sent;

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? MessageText { get; set; }

    [MaxLength(255)]
    public string? AttachmentName { get; set; }

    [MaxLength(200)]
    public string? BotMessageId { get; set; }

    [MaxLength(200)]
    public string? SentByUserId { get; set; }

    [MaxLength(200)]
    public string? SentByUserName { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}
