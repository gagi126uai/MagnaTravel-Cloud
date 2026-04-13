using TravelApi.Application.Contracts;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IAuditService
{
    /// <summary>
    /// Consulta logs de auditoria para una entidad especifica (uso en timelines de detalle).
    /// </summary>
    Task<IEnumerable<AuditLog>> GetAuditLogsAsync(
        string? entityName,
        string? entityId,
        string? alternateEntityId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? userId,
        CancellationToken ct);

    /// <summary>
    /// Consulta global paginada de logs de auditoria (uso en pantalla de auditoria).
    /// </summary>
    Task<PagedResult<AuditLog>> GetGlobalAuditLogsAsync(
        string? entityName,
        string? action,
        string? userId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken ct);
}
