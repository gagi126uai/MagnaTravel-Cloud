using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class WhatsAppDeliveryService : IWhatsAppDeliveryService
{
    private const string OperationalAckMessage = "Recibimos tu mensaje sobre tu reserva. Un asesor te respondera por este medio.";

    private readonly AppDbContext _db;
    private readonly IWhatsAppGateway _whatsAppGateway;
    private readonly ILogger<WhatsAppDeliveryService> _logger;

    public WhatsAppDeliveryService(
        AppDbContext db,
        IWhatsAppGateway whatsAppGateway,
        ILogger<WhatsAppDeliveryService> logger)
    {
        _db = db;
        _whatsAppGateway = whatsAppGateway;
        _logger = logger;
    }

    public async Task<bool> TryHandleIncomingOperationalMessageAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var normalizedPhone = WhatsAppPhoneHelper.NormalizeDigits(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return false;
        }

        var messageContext = await FindRecentMessageDeliveryContextAsync(normalizedPhone, cancellationToken);
        if (messageContext is not null)
        {
            await RecordIncomingMessageDeliveryAsync(messageContext, phone, message, cancellationToken);
            return true;
        }

        var legacyContext = await FindRecentLegacyDeliveryContextAsync(normalizedPhone, cancellationToken);
        if (legacyContext is not null)
        {
            await RecordIncomingLegacyDeliveryAsync(legacyContext, phone, message, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<MessageDelivery?> FindRecentMessageDeliveryContextAsync(string normalizedPhone, CancellationToken cancellationToken)
    {
        var recentOutbound = await _db.MessageDeliveries
            .Where(delivery =>
                delivery.Channel == MessageDeliveryChannels.WhatsApp &&
                delivery.Status == MessageDeliveryStatuses.Sent &&
                delivery.SentAt != null &&
                delivery.SentAt >= DateTime.UtcNow.AddDays(-30))
            .OrderByDescending(delivery => delivery.SentAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        return recentOutbound.FirstOrDefault(delivery => WhatsAppPhoneHelper.NormalizeDigits(delivery.Phone) == normalizedPhone);
    }

    private async Task<WhatsAppDelivery?> FindRecentLegacyDeliveryContextAsync(string normalizedPhone, CancellationToken cancellationToken)
    {
        var recentOutbound = await _db.WhatsAppDeliveries
            .Where(delivery =>
                delivery.Direction == WhatsAppDeliveryDirections.Outbound &&
                delivery.Status == WhatsAppDeliveryStatuses.Sent &&
                delivery.SentAt != null &&
                delivery.SentAt >= DateTime.UtcNow.AddDays(-30))
            .OrderByDescending(delivery => delivery.SentAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        return recentOutbound.FirstOrDefault(delivery => WhatsAppPhoneHelper.NormalizeDigits(delivery.Phone) == normalizedPhone);
    }

    private async Task RecordIncomingMessageDeliveryAsync(
        MessageDelivery context,
        string phone,
        string message,
        CancellationToken cancellationToken)
    {
        var canonicalPhone = WhatsAppPhoneHelper.Canonicalize(phone) ?? context.Phone;
        _db.MessageDeliveries.Add(new MessageDelivery
        {
            ReservaId = context.ReservaId,
            CustomerId = context.CustomerId,
            PassengerId = context.PassengerId,
            Channel = MessageDeliveryChannels.WhatsApp,
            Kind = MessageDeliveryKinds.IncomingMessage,
            Status = MessageDeliveryStatuses.NeedsAgent,
            Phone = canonicalPhone,
            MessageText = message,
            CreatedAt = DateTime.UtcNow
        });

        var alreadyAcked = await _db.MessageDeliveries.AnyAsync(
            delivery => delivery.ReservaId == context.ReservaId
                && delivery.Kind == MessageDeliveryKinds.OperationalAck
                && delivery.SentAt != null
                && delivery.SentAt >= DateTime.UtcNow.AddHours(-24),
            cancellationToken);

        if (!alreadyAcked)
        {
            try
            {
                var sendResult = await _whatsAppGateway.SendTextAsync(canonicalPhone, OperationalAckMessage, cancellationToken);
                _db.MessageDeliveries.Add(new MessageDelivery
                {
                    ReservaId = context.ReservaId,
                    CustomerId = context.CustomerId,
                    PassengerId = context.PassengerId,
                    Channel = MessageDeliveryChannels.WhatsApp,
                    Kind = MessageDeliveryKinds.OperationalAck,
                    Status = MessageDeliveryStatuses.Sent,
                    Phone = canonicalPhone,
                    MessageText = OperationalAckMessage,
                    BotMessageId = sendResult.MessageId,
                    SentByUserId = "System",
                    SentByUserName = "System",
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar el acuse operativo automatico para Reserva #{ReservaId}", context.ReservaId);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordIncomingLegacyDeliveryAsync(
        WhatsAppDelivery context,
        string phone,
        string message,
        CancellationToken cancellationToken)
    {
        var canonicalPhone = WhatsAppPhoneHelper.Canonicalize(phone) ?? context.Phone;
        _db.WhatsAppDeliveries.Add(new WhatsAppDelivery
        {
            ReservaId = context.ReservaId,
            CustomerId = context.CustomerId,
            Phone = canonicalPhone,
            Kind = WhatsAppDeliveryKinds.IncomingMessage,
            Direction = WhatsAppDeliveryDirections.Inbound,
            Status = WhatsAppDeliveryStatuses.NeedsAgent,
            MessageText = message,
            CreatedBy = "Cliente",
            CreatedAt = DateTime.UtcNow
        });

        var alreadyAcked = await _db.WhatsAppDeliveries.AnyAsync(
            delivery => delivery.ReservaId == context.ReservaId
                && delivery.Kind == WhatsAppDeliveryKinds.OperationalAck
                && delivery.Direction == WhatsAppDeliveryDirections.Outbound
                && delivery.SentAt != null
                && delivery.SentAt >= DateTime.UtcNow.AddHours(-24),
            cancellationToken);

        if (!alreadyAcked)
        {
            try
            {
                var sendResult = await _whatsAppGateway.SendTextAsync(canonicalPhone, OperationalAckMessage, cancellationToken);
                _db.WhatsAppDeliveries.Add(new WhatsAppDelivery
                {
                    ReservaId = context.ReservaId,
                    CustomerId = context.CustomerId,
                    Phone = canonicalPhone,
                    Kind = WhatsAppDeliveryKinds.OperationalAck,
                    Direction = WhatsAppDeliveryDirections.Outbound,
                    Status = WhatsAppDeliveryStatuses.Sent,
                    MessageText = OperationalAckMessage,
                    BotMessageId = sendResult.MessageId,
                    CreatedBy = "System",
                    SentBy = "System",
                    CreatedAt = DateTime.UtcNow,
                    PreparedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar el acuse operativo automatico legado para Reserva #{ReservaId}", context.ReservaId);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
