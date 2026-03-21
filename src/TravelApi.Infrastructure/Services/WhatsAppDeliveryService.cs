using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class WhatsAppDeliveryService : IWhatsAppDeliveryService
{
    private const string OperationalAckMessage = "Recibimos tu mensaje sobre tu reserva. Un asesor te respondera por este medio.";

    private readonly AppDbContext _db;
    private readonly IVoucherService _voucherService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMapper _mapper;
    private readonly ILogger<WhatsAppDeliveryService> _logger;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public WhatsAppDeliveryService(
        AppDbContext db,
        IVoucherService voucherService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMapper mapper,
        ILogger<WhatsAppDeliveryService> logger,
        IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _db = db;
        _voucherService = voucherService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _mapper = mapper;
        _logger = logger;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
    }

    public async Task PrepareVoucherDraftAsync(int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await GetReservaForWhatsAppAsync(reservaId, cancellationToken);
        var (resolvedPhone, _) = ResolvePhone(reserva);
        var existingDraft = await GetPendingVoucherDraftAsync(reservaId, cancellationToken);

        if (string.IsNullOrWhiteSpace(resolvedPhone))
        {
            if (existingDraft != null)
            {
                _db.WhatsAppDeliveries.Remove(existingDraft);
                await _db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var caption = BuildVoucherCaption(reserva);
        var attachmentName = BuildVoucherFileName(reserva);

        if (existingDraft == null)
        {
            existingDraft = new WhatsAppDelivery
            {
                ReservaId = reserva.Id,
                CustomerId = reserva.PayerId,
                Phone = resolvedPhone,
                Kind = WhatsAppDeliveryKinds.Voucher,
                Direction = WhatsAppDeliveryDirections.Outbound,
                Status = WhatsAppDeliveryStatuses.PendingApproval,
                MessageText = caption,
                AttachmentName = attachmentName,
                CreatedBy = "System",
                CreatedAt = DateTime.UtcNow,
                PreparedAt = DateTime.UtcNow
            };
            _db.WhatsAppDeliveries.Add(existingDraft);
        }
        else
        {
            existingDraft.CustomerId = reserva.PayerId;
            existingDraft.Phone = resolvedPhone;
            existingDraft.MessageText = caption;
            existingDraft.AttachmentName = attachmentName;
            existingDraft.Error = null;
            existingDraft.PreparedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WhatsAppVoucherPreviewResponse> GetVoucherPreviewAsync(int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await GetReservaForWhatsAppAsync(reservaId, cancellationToken);
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var (resolvedPhone, source) = ResolvePhone(reserva);
        var draft = await GetPendingVoucherDraftAsync(reservaId, cancellationToken);
        var blockReason = EconomicRulesHelper.GetVoucherBlockReason(reserva, settings);
        var canSend = string.IsNullOrWhiteSpace(blockReason) && !string.IsNullOrWhiteSpace(resolvedPhone);

        var response = new WhatsAppVoucherPreviewResponse
        {
            CanSend = canSend,
            ResolvedPhone = resolvedPhone,
            PhoneSource = source,
            PhoneOverride = reserva.WhatsAppPhoneOverride,
            Caption = draft?.MessageText ?? BuildVoucherCaption(reserva),
            AttachmentName = draft?.AttachmentName ?? BuildVoucherFileName(reserva),
            Error = !string.IsNullOrWhiteSpace(blockReason)
                ? blockReason
                : string.IsNullOrWhiteSpace(resolvedPhone)
                    ? "No hay un telefono de WhatsApp disponible para esta reserva."
                    : null
        };

        return response;
    }

    public async Task<WhatsAppVoucherPreviewResponse> UpdateReservaWhatsAppContactAsync(int reservaId, string? phoneOverride, CancellationToken cancellationToken)
    {
        var reserva = await _db.Reservas
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException("Reserva no encontrada.");

        reserva.WhatsAppPhoneOverride = WhatsAppPhoneHelper.Canonicalize(phoneOverride);
        await _db.SaveChangesAsync(cancellationToken);
        await PrepareVoucherDraftAsync(reservaId, cancellationToken);
        return await GetVoucherPreviewAsync(reservaId, cancellationToken);
    }

    public async Task<IReadOnlyList<WhatsAppDeliveryDto>> GetHistoryAsync(int reservaId, CancellationToken cancellationToken)
    {
        var items = await _db.WhatsAppDeliveries
            .Where(d => d.ReservaId == reservaId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        return items.Select(_mapper.Map<WhatsAppDeliveryDto>).ToList();
    }

    public async Task<WhatsAppDeliveryDto> SendVoucherAsync(int reservaId, string? caption, string performedBy, CancellationToken cancellationToken)
    {
        var reserva = await GetReservaForWhatsAppAsync(reservaId, cancellationToken);
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var blockReason = EconomicRulesHelper.GetVoucherBlockReason(reserva, settings);
        if (!string.IsNullOrWhiteSpace(blockReason))
            throw new InvalidOperationException(blockReason);

        var (resolvedPhone, source) = ResolvePhone(reserva);
        if (string.IsNullOrWhiteSpace(resolvedPhone))
            throw new InvalidOperationException("No hay un telefono disponible para enviar el voucher por WhatsApp.");

        var draft = await GetPendingVoucherDraftAsync(reservaId, cancellationToken);
        var finalCaption = string.IsNullOrWhiteSpace(caption)
            ? draft?.MessageText ?? BuildVoucherCaption(reserva)
            : caption.Trim();
        var attachmentName = BuildVoucherFileName(reserva);

        if (draft == null)
        {
            draft = new WhatsAppDelivery
            {
                ReservaId = reserva.Id,
                CustomerId = reserva.PayerId,
                Kind = WhatsAppDeliveryKinds.Voucher,
                Direction = WhatsAppDeliveryDirections.Outbound,
                Status = WhatsAppDeliveryStatuses.PendingApproval,
                CreatedBy = "System",
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppDeliveries.Add(draft);
        }

        draft.Phone = resolvedPhone;
        draft.CustomerId = reserva.PayerId;
        draft.MessageText = finalCaption;
        draft.AttachmentName = attachmentName;
        draft.PreparedAt ??= DateTime.UtcNow;
        draft.SentBy = performedBy;
        draft.Error = null;

        var pdfBytes = await _voucherService.GenerateVoucherPdfAsync(reservaId, cancellationToken);

        try
        {
            var responseBody = await SendDocumentAsync(
                resolvedPhone,
                finalCaption,
                attachmentName,
                "application/pdf",
                Convert.ToBase64String(pdfBytes),
                cancellationToken);

            draft.Status = WhatsAppDeliveryStatuses.Sent;
            draft.SentAt = DateTime.UtcNow;
            draft.BotMessageId = TryExtractMessageId(responseBody);
            draft.Error = null;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Voucher enviado por WhatsApp para Reserva #{ReservaId} via {PhoneSource} hacia {Phone}",
                reservaId,
                source,
                resolvedPhone);

            return _mapper.Map<WhatsAppDeliveryDto>(draft);
        }
        catch (Exception ex)
        {
            draft.Status = WhatsAppDeliveryStatuses.Failed;
            draft.Error = ex.Message;
            draft.SentAt = null;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Error enviando voucher por WhatsApp para Reserva #{ReservaId}", reservaId);
            throw;
        }
    }

    public async Task<bool> TryHandleIncomingOperationalMessageAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var normalizedPhone = WhatsAppPhoneHelper.NormalizeDigits(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return false;

        var recentOutbound = await _db.WhatsAppDeliveries
            .Where(d =>
                d.Direction == WhatsAppDeliveryDirections.Outbound &&
                d.Status == WhatsAppDeliveryStatuses.Sent &&
                d.SentAt != null &&
                d.SentAt >= DateTime.UtcNow.AddDays(-30))
            .OrderByDescending(d => d.SentAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var context = recentOutbound.FirstOrDefault(d => WhatsAppPhoneHelper.NormalizeDigits(d.Phone) == normalizedPhone);
        if (context == null)
            return false;

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
            d => d.ReservaId == context.ReservaId
                && d.Kind == WhatsAppDeliveryKinds.OperationalAck
                && d.Direction == WhatsAppDeliveryDirections.Outbound
                && d.SentAt != null
                && d.SentAt >= DateTime.UtcNow.AddHours(-24),
            cancellationToken);

        if (!alreadyAcked)
        {
            try
            {
                var responseBody = await SendTextAsync(canonicalPhone, OperationalAckMessage, cancellationToken);
                _db.WhatsAppDeliveries.Add(new WhatsAppDelivery
                {
                    ReservaId = context.ReservaId,
                    CustomerId = context.CustomerId,
                    Phone = canonicalPhone,
                    Kind = WhatsAppDeliveryKinds.OperationalAck,
                    Direction = WhatsAppDeliveryDirections.Outbound,
                    Status = WhatsAppDeliveryStatuses.Sent,
                    MessageText = OperationalAckMessage,
                    BotMessageId = TryExtractMessageId(responseBody),
                    CreatedBy = "System",
                    SentBy = "System",
                    CreatedAt = DateTime.UtcNow,
                    PreparedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar el acuse operativo automatico para Reserva #{ReservaId}", context.ReservaId);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Reserva> GetReservaForWhatsAppAsync(int reservaId, CancellationToken cancellationToken)
    {
        return await _db.Reservas
            .Include(r => r.Payer)
            .Include(r => r.SourceLead)
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken)
            ?? throw new KeyNotFoundException("Reserva no encontrada.");
    }

    private async Task<WhatsAppDelivery?> GetPendingVoucherDraftAsync(int reservaId, CancellationToken cancellationToken)
    {
        return await _db.WhatsAppDeliveries
            .Where(d =>
                d.ReservaId == reservaId &&
                d.Kind == WhatsAppDeliveryKinds.Voucher &&
                d.Direction == WhatsAppDeliveryDirections.Outbound &&
                d.Status == WhatsAppDeliveryStatuses.PendingApproval)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string BuildVoucherCaption(Reserva reserva)
    {
        var passengerName = reserva.Payer?.FullName;
        if (string.IsNullOrWhiteSpace(passengerName))
        {
            return $"Hola, te compartimos el voucher de tu reserva {reserva.NumeroReserva}. Si tenes alguna duda, respondenos por este medio y un asesor te ayuda.";
        }

        return $"Hola {passengerName}, te compartimos el voucher de tu reserva {reserva.NumeroReserva}. Si tenes alguna duda, respondenos por este medio y un asesor te ayuda.";
    }

    private static string BuildVoucherFileName(Reserva reserva)
    {
        return $"voucher-{reserva.NumeroReserva}.pdf";
    }

    private static (string? Phone, string Source) ResolvePhone(Reserva reserva)
    {
        var overridePhone = WhatsAppPhoneHelper.Canonicalize(reserva.WhatsAppPhoneOverride);
        if (!string.IsNullOrWhiteSpace(overridePhone))
            return (overridePhone, "override");

        var payerPhone = WhatsAppPhoneHelper.Canonicalize(reserva.Payer?.Phone);
        if (!string.IsNullOrWhiteSpace(payerPhone))
            return (payerPhone, "payer");

        var leadPhone = WhatsAppPhoneHelper.Canonicalize(reserva.SourceLead?.Phone);
        if (!string.IsNullOrWhiteSpace(leadPhone))
            return (leadPhone, "lead");

        return (null, "none");
    }

    private async Task<string> SendTextAsync(string phone, string message, CancellationToken cancellationToken)
    {
        var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{botUrl}/send")
        {
            Content = JsonContent.Create(new
            {
                phone,
                message
            })
        };
        request.Headers.Add("X-Webhook-Secret", secret);

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "Error enviando mensaje por WhatsApp." : body);

        return body;
    }

    private async Task<string> SendDocumentAsync(
        string phone,
        string caption,
        string fileName,
        string mimeType,
        string base64,
        CancellationToken cancellationToken)
    {
        var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{botUrl}/send-document")
        {
            Content = JsonContent.Create(new
            {
                phone,
                caption,
                fileName,
                mimeType,
                base64
            })
        };
        request.Headers.Add("X-Webhook-Secret", secret);

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? "Error enviando documento por WhatsApp." : body);

        return body;
    }

    private static string? TryExtractMessageId(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("messageId", out var messageId))
                return messageId.GetString();
        }
        catch
        {
            // Ignore malformed responses from the bot.
        }

        return null;
    }
}
