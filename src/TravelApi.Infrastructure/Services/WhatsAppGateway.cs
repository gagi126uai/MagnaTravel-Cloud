using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class WhatsAppGateway : IWhatsAppGateway
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public WhatsAppGateway(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<WhatsAppSendResult> SendTextAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var body = await SendToBotAsync("/send", new { phone, message }, TimeSpan.FromSeconds(15), cancellationToken);
        return ParseSendResult(body);
    }

    public async Task<WhatsAppSendResult> SendDocumentAsync(
        string phone,
        string caption,
        string fileName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var body = await SendToBotAsync(
            "/send-document",
            new
            {
                phone,
                caption,
                fileName,
                mimeType,
                base64 = Convert.ToBase64String(bytes)
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

        return ParseSendResult(body);
    }

    private async Task<string> SendToBotAsync(string path, object payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{botUrl.TrimEnd('/')}{path}")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Webhook-Secret", secret);

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body)
                ? "Error enviando mensaje por WhatsApp."
                : body);
        }

        return body;
    }

    private static WhatsAppSendResult ParseSendResult(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new WhatsAppSendResult(true, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var messageId = root.TryGetProperty("messageId", out var messageIdProperty)
                ? messageIdProperty.GetString()
                : null;
            return new WhatsAppSendResult(true, messageId, null);
        }
        catch
        {
            return new WhatsAppSendResult(true, null, null);
        }
    }
}
