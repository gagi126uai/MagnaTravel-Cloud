using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Audit;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class AuditLogsController : ControllerBase
{
    private static readonly HashSet<string> SensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PasswordHash",
        "SecurityStamp",
        "ConcurrencyStamp",
        "CertificateData",
        "CertificatePassword",
        "Token",
        "Sign",
        "PadronToken",
        "PadronSign",
        "TokenHash",
        "ReplacedByTokenHash",
        "RefreshToken",
        "WebhookSecret",
        "CsrfToken",
        "AccessToken"
    };

    private readonly IAuditService _auditService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public AuditLogsController(IAuditService auditService, EntityReferenceResolver entityReferenceResolver)
    {
        _auditService = auditService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditLogResponse>>> GetAuditLogs(
        [FromQuery] string? entityName,
        [FromQuery] string? entityId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? userId,
        CancellationToken ct)
    {
        var alternateEntityId = await ResolveAlternateEntityIdAsync(entityName, entityId, ct);
        var logs = await _auditService.GetAuditLogsAsync(entityName, entityId, alternateEntityId, dateFrom, dateTo, userId, ct);
        return Ok(logs.Select(MapResponse));
    }

    private async Task<string?> ResolveAlternateEntityIdAsync(string? entityName, string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        return entityName switch
        {
            nameof(Reserva) => await ResolveInternalIdAsync<Reserva>(entityId, ct),
            nameof(Customer) => await ResolveInternalIdAsync<Customer>(entityId, ct),
            nameof(Supplier) => await ResolveInternalIdAsync<Supplier>(entityId, ct),
            nameof(Lead) => await ResolveInternalIdAsync<Lead>(entityId, ct),
            nameof(Quote) => await ResolveInternalIdAsync<Quote>(entityId, ct),
            nameof(Payment) => await ResolveInternalIdAsync<Payment>(entityId, ct),
            nameof(Invoice) => await ResolveInternalIdAsync<Invoice>(entityId, ct),
            nameof(Passenger) => await ResolveInternalIdAsync<Passenger>(entityId, ct),
            nameof(ServicioReserva) => await ResolveInternalIdAsync<ServicioReserva>(entityId, ct),
            nameof(FlightSegment) => await ResolveInternalIdAsync<FlightSegment>(entityId, ct),
            nameof(HotelBooking) => await ResolveInternalIdAsync<HotelBooking>(entityId, ct),
            nameof(PackageBooking) => await ResolveInternalIdAsync<PackageBooking>(entityId, ct),
            nameof(TransferBooking) => await ResolveInternalIdAsync<TransferBooking>(entityId, ct),
            nameof(ReservaAttachment) => await ResolveInternalIdAsync<ReservaAttachment>(entityId, ct),
            _ => null
        };
    }

    private async Task<string?> ResolveInternalIdAsync<TEntity>(string entityId, CancellationToken ct)
        where TEntity : class, IHasPublicId
    {
        var entity = await _entityReferenceResolver.FindAsync<TEntity>(entityId, ct);
        return entity switch
        {
            Customer customer => customer.Id.ToString(),
            Supplier supplier => supplier.Id.ToString(),
            Reserva reserva => reserva.Id.ToString(),
            Lead lead => lead.Id.ToString(),
            Quote quote => quote.Id.ToString(),
            Payment payment => payment.Id.ToString(),
            Invoice invoice => invoice.Id.ToString(),
            Passenger passenger => passenger.Id.ToString(),
            ServicioReserva servicio => servicio.Id.ToString(),
            FlightSegment flight => flight.Id.ToString(),
            HotelBooking hotel => hotel.Id.ToString(),
            PackageBooking packageBooking => packageBooking.Id.ToString(),
            TransferBooking transfer => transfer.Id.ToString(),
            ReservaAttachment attachment => attachment.Id.ToString(),
            _ => null
        };
    }

    private static AuditLogResponse MapResponse(AuditLog log)
    {
        return new AuditLogResponse(
            log.Id,
            log.UserId,
            log.UserName,
            log.Action,
            log.EntityName,
            log.EntityId,
            log.Timestamp,
            RedactChanges(log.Changes));
    }

    private static string? RedactChanges(string? rawChanges)
    {
        if (string.IsNullOrWhiteSpace(rawChanges))
        {
            return rawChanges;
        }

        try
        {
            var node = JsonNode.Parse(rawChanges);
            if (node is null)
            {
                return rawChanges;
            }

            RedactNode(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return "[REDACTED_UNPARSEABLE_AUDIT_PAYLOAD]";
        }
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Key is not null && SensitiveFields.Contains(property.Key))
                {
                    obj[property.Key] = "[REDACTED]";
                    continue;
                }

                if (property.Value is not null)
                {
                    RedactNode(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RedactNode(item);
                }
            }
        }
    }
}
