namespace TravelApi.Application.Interfaces;

public record WhatsAppSendResult(bool Success, string? MessageId, string? Error);

public interface IWhatsAppGateway
{
    Task<WhatsAppSendResult> SendTextAsync(
        string phone,
        string message,
        CancellationToken cancellationToken);

    Task<WhatsAppSendResult> SendDocumentAsync(
        string phone,
        string caption,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken);
}
