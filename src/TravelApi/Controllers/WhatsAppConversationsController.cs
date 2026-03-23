using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Controllers;

[ApiController]
[Authorize]
[Route("api/whatsapp/conversations")]
public class WhatsAppConversationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public WhatsAppConversationsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WhatsAppConversationListItemDto>>> GetConversations(CancellationToken cancellationToken)
    {
        var leads = await _db.Leads
            .AsNoTracking()
            .Include(lead => lead.Activities)
            .Where(lead => lead.Activities.Any(activity => activity.Type == "WhatsApp"))
            .ToListAsync(cancellationToken);

        var deliveries = await _db.WhatsAppDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Reserva)
            .ThenInclude(reserva => reserva!.Payer)
            .Where(delivery => delivery.Status != WhatsAppDeliveryStatuses.PendingApproval)
            .ToListAsync(cancellationToken);

        var leadItems = leads
            .Select(BuildLeadConversationListItem)
            .Where(item => item != null)
            .Cast<WhatsAppConversationListItemDto>();

        var operationalItems = deliveries
            .GroupBy(delivery => delivery.ReservaId)
            .Select(BuildOperationalConversationListItem)
            .Where(item => item != null)
            .Cast<WhatsAppConversationListItemDto>();

        var items = leadItems
            .Concat(operationalItems)
            .OrderByDescending(item => item.LastMessageAt)
            .ToList();

        return Ok(items);
    }

    [HttpGet("{conversationType}/{entityId:int}")]
    public async Task<ActionResult<WhatsAppConversationDetailDto>> GetConversationDetail(
        string conversationType,
        int entityId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(conversationType, "lead", StringComparison.OrdinalIgnoreCase))
        {
            var lead = await _db.Leads
                .AsNoTracking()
                .Include(item => item.Activities)
                .FirstOrDefaultAsync(item => item.Id == entityId, cancellationToken);

            if (lead == null)
                return NotFound();

            var messages = BuildLeadMessages(lead);
            if (messages.Count == 0)
                return NotFound();

            return Ok(new WhatsAppConversationDetailDto
            {
                ConversationType = "lead",
                EntityId = lead.Id,
                LeadId = lead.Id,
                Phone = lead.Phone ?? string.Empty,
                Title = BuildLeadTitle(lead),
                Subtitle = BuildLeadSubtitle(lead),
                StatusLabel = lead.Status,
                InterestedIn = lead.InterestedIn,
                TravelDates = lead.TravelDates,
                Travelers = lead.Travelers,
                Messages = messages
            });
        }

        if (string.Equals(conversationType, "operational", StringComparison.OrdinalIgnoreCase))
        {
            var deliveries = await _db.WhatsAppDeliveries
                .AsNoTracking()
                .Include(delivery => delivery.Reserva)
                .ThenInclude(reserva => reserva!.Payer)
                .Where(delivery =>
                    delivery.ReservaId == entityId &&
                    delivery.Status != WhatsAppDeliveryStatuses.PendingApproval)
                .OrderBy(delivery => delivery.CreatedAt)
                .ToListAsync(cancellationToken);

            if (deliveries.Count == 0)
                return NotFound();

            var reserva = deliveries.First().Reserva;
            if (reserva == null)
                return NotFound();

            return Ok(new WhatsAppConversationDetailDto
            {
                ConversationType = "operational",
                EntityId = reserva.Id,
                ReservaId = reserva.Id,
                LeadId = reserva.SourceLeadId,
                Phone = deliveries.Last().Phone,
                Title = $"Reserva {reserva.NumeroReserva}",
                Subtitle = reserva.Payer?.FullName ?? reserva.Name,
                StatusLabel = reserva.Status,
                Messages = deliveries.Select(BuildOperationalMessage).ToList()
            });
        }

        return BadRequest(new { message = "Tipo de conversacion invalido." });
    }

    private static WhatsAppConversationListItemDto? BuildLeadConversationListItem(Lead lead)
    {
        var messages = BuildLeadMessages(lead);
        if (messages.Count == 0)
            return null;

        var lastMessage = messages[^1];

        return new WhatsAppConversationListItemDto
        {
            ConversationType = "lead",
            EntityId = lead.Id,
            LeadId = lead.Id,
            Phone = lead.Phone ?? string.Empty,
            Title = BuildLeadTitle(lead),
            Subtitle = BuildLeadSubtitle(lead),
            StatusLabel = lead.Status,
            LastMessagePreview = BuildPreview(lastMessage.Text),
            LastMessageAt = lastMessage.CreatedAt,
            NeedsAttention = lastMessage.Sender == "client",
            MessageCount = messages.Count
        };
    }

    private static WhatsAppConversationListItemDto? BuildOperationalConversationListItem(IGrouping<int, WhatsAppDelivery> group)
    {
        var deliveries = group.OrderBy(item => item.CreatedAt).ToList();
        if (deliveries.Count == 0)
            return null;

        var latest = deliveries[^1];
        var reserva = latest.Reserva;
        if (reserva == null)
            return null;

        return new WhatsAppConversationListItemDto
        {
            ConversationType = "operational",
            EntityId = reserva.Id,
            ReservaId = reserva.Id,
            LeadId = reserva.SourceLeadId,
            Phone = latest.Phone,
            Title = $"Reserva {reserva.NumeroReserva}",
            Subtitle = reserva.Payer?.FullName ?? reserva.Name,
            StatusLabel = latest.Status == WhatsAppDeliveryStatuses.NeedsAgent ? "Requiere seguimiento" : reserva.Status,
            LastMessagePreview = BuildPreview(BuildOperationalMessageText(latest)),
            LastMessageAt = latest.SentAt ?? latest.CreatedAt,
            NeedsAttention = latest.Direction == WhatsAppDeliveryDirections.Inbound || latest.Status == WhatsAppDeliveryStatuses.NeedsAgent,
            MessageCount = deliveries.Count
        };
    }

    private static List<WhatsAppConversationMessageDto> BuildLeadMessages(Lead lead)
    {
        var messages = new List<WhatsAppConversationMessageDto>();

        foreach (var activity in lead.Activities
                     .Where(item => item.Type == "WhatsApp")
                     .OrderBy(item => item.CreatedAt))
        {
            if (string.Equals(activity.CreatedBy, "WhatsApp Bot", StringComparison.OrdinalIgnoreCase))
            {
                messages.AddRange(ParseTranscriptMessages(activity, lead.FullName));
                continue;
            }

            var isAgent =
                !string.IsNullOrWhiteSpace(activity.CreatedBy) &&
                !activity.CreatedBy.StartsWith("WhatsApp (", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(activity.CreatedBy, "Cliente", StringComparison.OrdinalIgnoreCase);

            messages.Add(new WhatsAppConversationMessageDto
            {
                Id = $"lead-{lead.Id}-activity-{activity.Id}",
                Sender = isAgent ? "agent" : "client",
                SenderLabel = isAgent
                    ? (activity.CreatedBy ?? "Agente")
                    : lead.FullName,
                Text = activity.Description,
                CreatedAt = activity.CreatedAt,
                Kind = "message"
            });
        }

        return messages;
    }

    private static IEnumerable<WhatsAppConversationMessageDto> ParseTranscriptMessages(LeadActivity activity, string leadName)
    {
        var lines = activity.Description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("Conversacion capturada por bot:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Nueva conversacion con bot:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("[Cliente]:", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WhatsAppConversationMessageDto
                {
                    Id = $"activity-{activity.Id}-client-{line.GetHashCode()}",
                    Sender = "client",
                    SenderLabel = leadName,
                    Text = line["[Cliente]:".Length..].Trim(),
                    CreatedAt = activity.CreatedAt,
                    Kind = "message"
                };
                continue;
            }

            if (line.StartsWith("[Bot]:", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WhatsAppConversationMessageDto
                {
                    Id = $"activity-{activity.Id}-bot-{line.GetHashCode()}",
                    Sender = "bot",
                    SenderLabel = "Bot",
                    Text = line["[Bot]:".Length..].Trim(),
                    CreatedAt = activity.CreatedAt,
                    Kind = "message"
                };
            }
        }
    }

    private static WhatsAppConversationMessageDto BuildOperationalMessage(WhatsAppDelivery delivery)
    {
        var isInbound = delivery.Direction == WhatsAppDeliveryDirections.Inbound;
        var isAgent = !isInbound &&
            !string.Equals(delivery.SentBy, "System", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(delivery.CreatedBy, "System", StringComparison.OrdinalIgnoreCase);

        return new WhatsAppConversationMessageDto
        {
            Id = $"operational-{delivery.Id}",
            Sender = isInbound ? "client" : isAgent ? "agent" : "bot",
            SenderLabel = isInbound
                ? "Cliente"
                : isAgent
                    ? (delivery.SentBy ?? delivery.CreatedBy ?? "Agente")
                    : "Bot",
            Text = BuildOperationalMessageText(delivery),
            CreatedAt = delivery.SentAt ?? delivery.CreatedAt,
            Kind = delivery.Kind
        };
    }

    private static string BuildOperationalMessageText(WhatsAppDelivery delivery)
    {
        if (!string.IsNullOrWhiteSpace(delivery.MessageText))
            return delivery.MessageText!;

        if (!string.IsNullOrWhiteSpace(delivery.AttachmentName))
            return $"Adjunto enviado: {delivery.AttachmentName}";

        if (!string.IsNullOrWhiteSpace(delivery.Error))
            return $"Error: {delivery.Error}";

        return "Movimiento registrado por WhatsApp.";
    }

    private static string BuildLeadTitle(Lead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.FullName) &&
            !lead.FullName.StartsWith("Nuevo contacto WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            return lead.FullName;
        }

        return !string.IsNullOrWhiteSpace(lead.Phone)
            ? lead.Phone
            : $"Lead #{lead.Id}";
    }

    private static string BuildLeadSubtitle(Lead lead)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(lead.InterestedIn))
            parts.Add(lead.InterestedIn);

        if (!string.IsNullOrWhiteSpace(lead.Phone))
            parts.Add(lead.Phone);

        parts.Add($"Pipeline: {lead.Status}");
        return string.Join(" · ", parts);
    }

    private static string? BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var clean = text.Trim().Replace("\n", " ");
        return clean.Length <= 90 ? clean : $"{clean[..87]}...";
    }
}
