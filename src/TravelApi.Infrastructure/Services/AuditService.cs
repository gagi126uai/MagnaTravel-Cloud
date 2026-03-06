using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;

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
            query = query.Where(l => l.EntityId == entityId);
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
}
