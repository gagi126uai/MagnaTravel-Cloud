using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IAuditService
{
    Task<IEnumerable<AuditLog>> GetAuditLogsAsync(
        string? entityName,
        string? entityId,
        string? alternateEntityId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? userId,
        CancellationToken ct);
}
