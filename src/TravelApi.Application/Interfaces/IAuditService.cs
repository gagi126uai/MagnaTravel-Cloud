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
        string? category,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>
    /// Registra un evento de negocio manualmente (login, exportacion, etc).
    /// </summary>
    Task LogBusinessEventAsync(
        string action,
        string entityName,
        string entityId,
        string? details,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Igual que <see cref="LogBusinessEventAsync"/> pero NO guarda: solo deja el AuditLog "para insertar"
    /// en el contexto compartido. Sirve cuando la auditoria tiene que entrar en la MISMA transaccion que la
    /// mutacion que la origina (ej. la cascada de borrado de asignaciones de ADR-031 §4.3): el caller cierra
    /// la mutacion + esta auditoria con un unico SaveChanges -> atomico. Con <see cref="LogBusinessEventAsync"/>
    /// se rompia la atomicidad porque guardaba en una transaccion separada ANTES del SaveChanges del caller.
    /// </summary>
    void StageBusinessEvent(
        string action,
        string entityName,
        string entityId,
        string? details,
        string userId,
        string? userName);
}
