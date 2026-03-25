namespace TravelApi.Application.Contracts.Audit;

public record AuditLogResponse(
    int Id,
    string UserId,
    string? UserName,
    string Action,
    string EntityName,
    string EntityId,
    DateTime Timestamp,
    string? Changes);
