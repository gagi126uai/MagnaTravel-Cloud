using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly IRepository<AuditLog> _auditRepo;

    public AuditService(IRepository<AuditLog> auditRepo)
    {
        _auditRepo = auditRepo;
    }

    public async Task<IEnumerable<AuditLog>> GetAuditLogsAsync(
        string? entityName,
        string? entityId,
        string? alternateEntityId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? userId,
        CancellationToken ct)
    {
        var query = _auditRepo.Query();

        if (!string.IsNullOrEmpty(entityName))
        {
            query = query.Where(l => l.EntityName == entityName);
        }

        if (!string.IsNullOrEmpty(entityId))
        {
            if (string.IsNullOrEmpty(alternateEntityId))
            {
                query = query.Where(l => l.EntityId == entityId);
            }
            else
            {
                query = query.Where(l => l.EntityId == entityId || l.EntityId == alternateEntityId);
            }
        }

        if (dateFrom.HasValue)
        {
            query = query.Where(l => l.Timestamp >= dateFrom.Value.ToUniversalTime());
        }

        if (dateTo.HasValue)
        {
            query = query.Where(l => l.Timestamp <= dateTo.Value.ToUniversalTime());
        }

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(l => l.UserId == userId);
        }

        return await query.OrderByDescending(l => l.Timestamp)
                          .Take(100)
                          .ToListAsync(ct);
    }

    public async Task<PagedResult<AuditLog>> GetGlobalAuditLogsAsync(
        string? entityName,
        string? action,
        string? userId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? searchTerm,
        string? category,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = _auditRepo.Query();

        // Filtrar por categoría (operativo vs sistema)
        if (!string.IsNullOrEmpty(category))
        {
            if (category == "operational")
            {
                query = query.Where(l => OperationalEntities.Contains(l.EntityName) || l.Category == "Business");
            }
            else if (category == "system")
            {
                query = query.Where(l => !OperationalEntities.Contains(l.EntityName) && l.Category != "Business");
            }
        }

        if (!string.IsNullOrEmpty(entityName))
        {
            query = query.Where(l => l.EntityName == entityName);
        }

        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(l => l.Action == action);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(l => l.UserId == userId);
        }

        if (dateFrom.HasValue)
        {
            query = query.Where(l => l.Timestamp >= dateFrom.Value.ToUniversalTime());
        }

        if (dateTo.HasValue)
        {
            var endOfDay = dateTo.Value.Date.AddDays(1).ToUniversalTime();
            query = query.Where(l => l.Timestamp < endOfDay);
        }

        if (!string.IsNullOrEmpty(searchTerm))
        {
            var term = searchTerm.ToLower();
            query = query.Where(l =>
                l.EntityName.ToLower().Contains(term) ||
                (l.UserName != null && l.UserName.ToLower().Contains(term)) ||
                l.EntityId.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditLog>(items, totalCount, page, pageSize);
    }

    public async Task LogBusinessEventAsync(
        string action,
        string entityName,
        string entityId,
        string? details,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        var auditLog = new AuditLog
        {
            UserId = userId,
            UserName = userName,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            Timestamp = DateTime.UtcNow,
            Category = "Business",
            Changes = details
        };

        await _auditRepo.AddAsync(auditLog, ct);
    }

    /// <summary>
    /// Entidades consideradas "operativas" (visibles para usuarios de negocio).
    /// Todo lo que no esté aquí se considera "sistema".
    /// </summary>
    private static readonly HashSet<string> OperationalEntities = new(StringComparer.Ordinal)
    {
        "Reserva", "Customer", "Supplier", "Payment", "Invoice",
        "Passenger", "ServicioReserva", "FlightSegment", "HotelBooking",
        "PackageBooking", "TransferBooking", "Lead", "LeadActivity",
        "Quote", "QuoteItem", "ReservaAttachment", "SupplierPayment",
        "ManualCashMovement", "CommissionRule", "Rate",
        "PaymentReceipt", "InvoiceItem", "InvoiceTribute",
        "CatalogPackage", "CatalogPackageDeparture",
        "Country", "Destination", "DestinationDeparture",
        "WhatsAppDelivery",
        // Business events (login, export, etc.)
        "Session", "Report", "User"
    };
}
