using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class WhatsAppWebhookService : IWhatsAppWebhookService
{
    private const string WhatsAppPlaceholderPrefix = "Consulta por WhatsApp";

    private readonly ILeadService _leadService;
    private readonly IWhatsAppDeliveryService _whatsAppDeliveryService;
    private readonly AppDbContext _db;

    public WhatsAppWebhookService(
        ILeadService leadService,
        IWhatsAppDeliveryService whatsAppDeliveryService,
        AppDbContext db)
    {
        _leadService = leadService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
        _db = db;
    }

    public async Task<WhatsAppLeadWebhookResult> ProcessLeadCaptureAsync(
        WhatsAppWebhookDto dto,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Phone))
        {
            throw new ArgumentException("Nombre y telefono son obligatorios.");
        }

        var phoneToSearch = NormalizePhone(dto.Phone);
        var existingLead = await _db.Leads
            .Where(lead =>
                (lead.Phone == dto.Phone || lead.Phone == phoneToSearch) &&
                lead.Status != LeadStatus.Won &&
                lead.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingLead is not null)
        {
            MergeStructuredWhatsAppCapture(existingLead, dto);
            await _db.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(dto.Transcript))
            {
                await _leadService.AddActivityAsync(
                    existingLead.PublicId.ToString(),
                    new LeadActivityUpsertRequest(
                        "WhatsApp",
                        $"Nueva conversacion con bot:\n{SanitizeMessageContent(dto.Transcript)}",
                        "WhatsApp Bot"),
                    "WhatsApp Bot",
                    cancellationToken);
            }

            return new WhatsAppLeadWebhookResult
            {
                Created = false,
                LeadPublicId = existingLead.PublicId
            };
        }

        var created = await _leadService.CreateAsync(new LeadUpsertRequest(
            dto.Name.Trim(),
            null,
            dto.Phone.Trim(),
            "WhatsApp",
            dto.Interest?.Trim(),
            dto.Dates?.Trim(),
            dto.Travelers?.Trim(),
            0m,
            "Lead capturado por WhatsApp Bot.",
            null,
            null,
            null), cancellationToken);

        if (!string.IsNullOrWhiteSpace(dto.Transcript))
        {
            await _leadService.AddActivityAsync(
                created.PublicId.ToString(),
                new LeadActivityUpsertRequest(
                    "WhatsApp",
                    $"Conversacion capturada por bot:\n{SanitizeMessageContent(dto.Transcript)}",
                    "WhatsApp Bot"),
                "WhatsApp Bot",
                cancellationToken);
        }

        return new WhatsAppLeadWebhookResult
        {
            Created = true,
            LeadPublicId = created.PublicId,
            Name = created.FullName,
            Phone = created.Phone
        };
    }

    public async Task<WhatsAppIncomingMessageResult> ProcessIncomingMessageAsync(
        WhatsAppMessageDto dto,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Phone) || string.IsNullOrWhiteSpace(dto.Message))
        {
            throw new ArgumentException("Phone y message son obligatorios.");
        }

        if (dto.Sender == "Cliente")
        {
            var handledOperationally = await _whatsAppDeliveryService.TryHandleIncomingOperationalMessageAsync(
                dto.Phone,
                dto.Message,
                cancellationToken);

            if (handledOperationally)
            {
                return new WhatsAppIncomingMessageResult
                {
                    HandledBy = "operational"
                };
            }
        }

        var phoneToSearch = NormalizePhone(dto.Phone);
        var lead = await _db.Leads
            .Where(item =>
                (item.Phone == dto.Phone || item.Phone == phoneToSearch) &&
                item.Status != LeadStatus.Won &&
                item.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (lead is null)
        {
            if (dto.SkipLeadAutoCreation)
            {
                return new WhatsAppIncomingMessageResult
                {
                    HandledBy = "none",
                    AutoCreated = false,
                    AllowBotCapture = true
                };
            }

            var createdLead = await _leadService.CreateAsync(new LeadUpsertRequest(
                $"{WhatsAppPlaceholderPrefix} ({dto.Phone})",
                null,
                dto.Phone,
                "WhatsApp",
                null,
                null,
                null,
                0m,
                "Lead creado automaticamente al recibir un mensaje sin proceso de bot completado.",
                null,
                null,
                null), cancellationToken);

            lead = await _db.Leads.FirstAsync(item => item.PublicId == createdLead.PublicId, cancellationToken);
        }

        await _leadService.AddActivityAsync(
            lead.PublicId.ToString(),
            new LeadActivityUpsertRequest(
                "WhatsApp",
                SanitizeMessageContent(dto.Message, 1000),
                dto.Sender == "Cliente" ? $"WhatsApp ({lead.FullName})" : "Agente CRM"),
            dto.Sender == "Cliente" ? $"WhatsApp ({lead.FullName})" : "Agente CRM",
            cancellationToken);

        return new WhatsAppIncomingMessageResult
        {
            HandledBy = "lead",
            LeadPublicId = lead.PublicId,
            AutoCreated = lead.FullName.StartsWith(WhatsAppPlaceholderPrefix, StringComparison.OrdinalIgnoreCase),
            AllowBotCapture = LeadNeedsQualification(lead)
        };
    }

    private static string NormalizePhone(string phone) => phone.Replace("+", "").Trim();

    private static bool IsPlaceholderLeadName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.StartsWith(WhatsAppPlaceholderPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool LeadNeedsQualification(Lead lead) =>
        IsPlaceholderLeadName(lead.FullName) ||
        string.IsNullOrWhiteSpace(lead.InterestedIn) ||
        string.IsNullOrWhiteSpace(lead.TravelDates) ||
        string.IsNullOrWhiteSpace(lead.Travelers);

    private static string SanitizeMessageContent(string? value, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string(value.Where(ch => !char.IsControl(ch) || ch == '\r' || ch == '\n' || ch == '\t').ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static void MergeStructuredWhatsAppCapture(Lead lead, WhatsAppWebhookDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Name) && IsPlaceholderLeadName(lead.FullName))
        {
            lead.FullName = dto.Name.Trim();
        }

        if (string.IsNullOrWhiteSpace(lead.Phone) && !string.IsNullOrWhiteSpace(dto.Phone))
        {
            lead.Phone = dto.Phone.Trim();
        }

        if (string.IsNullOrWhiteSpace(lead.Source))
        {
            lead.Source = "WhatsApp";
        }

        if (!string.IsNullOrWhiteSpace(dto.Interest))
        {
            lead.InterestedIn = dto.Interest.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.Dates))
        {
            lead.TravelDates = dto.Dates.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.Travelers))
        {
            lead.Travelers = dto.Travelers.Trim();
        }

        if (string.IsNullOrWhiteSpace(lead.Notes))
        {
            lead.Notes = "Lead capturado por WhatsApp Bot.";
        }
    }
}
