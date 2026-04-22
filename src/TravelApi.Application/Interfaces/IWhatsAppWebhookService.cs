using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppWebhookService
{
    Task<WhatsAppLeadWebhookResult> ProcessLeadCaptureAsync(
        WhatsAppWebhookDto dto,
        CancellationToken cancellationToken);

    Task<WhatsAppIncomingMessageResult> ProcessIncomingMessageAsync(
        WhatsAppMessageDto dto,
        CancellationToken cancellationToken);
}
