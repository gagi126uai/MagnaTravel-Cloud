using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuditLogsController(
        IAuditService auditService,
        IEntityReferenceResolver entityReferenceResolver,
        UserManager<ApplicationUser> userManager)
    {
        _auditService = auditService;
        _entityReferenceResolver = entityReferenceResolver;
        _userManager = userManager;
    }

    /// <summary>
    /// Obtiene logs de auditoria para una entidad especifica (usado por AuditTimeline).
    /// </summary>
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

    /// <summary>
    /// Consulta global paginada de logs de auditoria (pantalla de auditoria).
    /// </summary>
    [HttpGet("global")]
    public async Task<IActionResult> GetGlobalAuditLogs(
        [FromQuery] string? entityName,
        [FromQuery] string? action,
        [FromQuery] string? userId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? searchTerm,
        [FromQuery] string? category,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var result = await _auditService.GetGlobalAuditLogsAsync(
            entityName, action, userId, dateFrom, dateTo, searchTerm, category, page, pageSize, ct);

        return Ok(new
        {
            items = result.Items.Select(MapResponse),
            result.TotalCount,
            result.Page,
            result.PageSize,
            result.TotalPages
        });
    }

    /// <summary>
    /// Lista de entidades unicas presentes en auditoria (para dropdown de filtro).
    /// </summary>
    [HttpGet("entities")]
    public IActionResult GetDistinctEntities()
    {
        // Optimizacion: consultar nombres de entidad distintos directamente
        // Por ahora devolvemos una lista fija basada en las entidades del sistema
        var entityNames = new[]
        {
            "Reserva", "Customer", "Supplier", "Payment", "Invoice",
            "Passenger", "ServicioReserva", "FlightSegment", "HotelBooking",
            "PackageBooking", "TransferBooking", "Lead", "Quote", "QuoteItem",
            "ReservaAttachment", "SupplierPayment", "ManualCashMovement",
            "ApplicationUser", "CommissionRule", "Rate",
            "AgencySettings", "OperationalFinanceSettings", "AfipSettings",
            "WhatsAppBotConfig", "CatalogPackage", "Country", "Destination"
        };

        return Ok(entityNames.OrderBy(n => n));
    }

    /// <summary>
    /// Lista usuarios para filtro de auditoria.
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await _userManager.Users
            .Select(u => new { u.Id, u.UserName, u.FullName })
            .OrderBy(u => u.FullName ?? u.UserName)
            .ToListAsync(ct);

        return Ok(users);
    }

    /// <summary>
    /// Exportar logs de auditoria a CSV con los mismos filtros de la vista global.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string? entityName,
        [FromQuery] string? action,
        [FromQuery] string? userId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? searchTerm,
        [FromQuery] string? category,
        CancellationToken ct = default)
    {
        // Exportar max 5000 registros para evitar timeout/memory
        var result = await _auditService.GetGlobalAuditLogsAsync(
            entityName, action, userId, dateFrom, dateTo, searchTerm, category, 1, 5000, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Fecha,Usuario,Accion,Entidad,ID Entidad,Cambios");

        foreach (var log in result.Items)
        {
            var date = log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var user = EscapeCsv(log.UserName ?? log.UserId);
            var actionStr = EscapeCsv(log.Action);
            var entity = EscapeCsv(log.EntityName);
            var entityId = EscapeCsv(log.EntityId);
            var changes = EscapeCsv(SummarizeChanges(log.Changes));

            sb.AppendLine($"{date},{user},{actionStr},{entity},{entityId},{changes}");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"auditoria_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
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

    private static string SummarizeChanges(string? rawChanges)
    {
        if (string.IsNullOrWhiteSpace(rawChanges))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(rawChanges);
            if (node is JsonObject obj)
            {
                var fields = obj.Select(p => p.Key);
                return string.Join("; ", fields);
            }
        }
        catch
        {
            // ignorar
        }

        return "[datos]";
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
