using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Contracts;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly IRepository<AuditLog> _auditRepo;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IRepository<AuditLog> auditRepo, ILogger<AuditService> logger)
    {
        _auditRepo = auditRepo;
        _logger = logger;
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
        var query = _auditRepo.QueryAsNoTracking();

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

        var query = _auditRepo.QueryAsNoTracking();

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

        try
        {
            await _auditRepo.AddAsync(auditLog, ct);
        }
        catch (BusinessInvariantViolationException)
        {
            // FC1.2.2 fix 2026-05-18: las violaciones de invariante de negocio
            // (CHECK constraint SQL via BusinessInvariantInterceptor — ej. INV-084
            // cap excedido) NO se traen. El SaveChanges del Repository.AddAsync
            // arrastra tracked entities Modified ademas del audit log: si una de
            // esas modificaciones rompe un CHECK constraint, el caller debe
            // recibir la excepcion para que su retry/rollback funcione. Tragar
            // INV-084 aca dejaba pasar over-allocations en concurrencia N:M.
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            // FC1.2.2 fix 2026-05-18: conflictos xmin (concurrencia optimista
            // de Postgres). El caller tiene retry loops que dependen de ver esta
            // excepcion para recargar y reintentar.
            throw;
        }
        catch (DbUpdateException)
        {
            // FC1.2.2 fix 2026-05-18: violaciones de FK/unique/etc. que el caller
            // necesita ver para abortar o decidir como recuperar. Si las tragamos,
            // dejamos persistir estados inconsistentes (ver bug FC1.2.2 reviewer).
            throw;
        }
        catch (Exception ex)
        {
            // Resto de errores (ej. JSON serialization de details, problemas de red
            // del repo): la auditoria NO debe romper la operacion principal.
            // Loggear y continuar — la operacion de negocio ya esta materializada
            // o lo estara en el SaveChanges del caller.
            _logger.LogError(ex, "Audit log fallo: action={Action} entity={Entity} entityId={EntityId} userId={UserId}",
                action, entityName, entityId, userId);
        }
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
        "WhatsAppDelivery", "Voucher", "VoucherPassengerAssignment",
        "VoucherAuditEntry", "MessageDelivery",
        // Business events (login, export, etc.)
        "Session", "Report", "User"
    };
}
