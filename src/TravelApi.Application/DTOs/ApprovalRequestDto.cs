namespace TravelApi.Application.DTOs;

/// <summary>
/// B1.15 Fase B' (2026-05-11): respuesta de un ApprovalRequest al cliente.
/// </summary>
public class ApprovalRequestDto
{
    public Guid PublicId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string RequestedByUserId { get; set; } = string.Empty;
    public string? RequestedByUserName { get; set; }
    public DateTime RequestedAt { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ResolvedByUserId { get; set; }
    public string? ResolvedByUserName { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolverNotes { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? CooldownUntil { get; set; }
    public string? Metadata { get; set; }
}

public record CreateApprovalRequestPayload(
    string RequestType,
    string EntityType,
    int EntityId,
    string? Reason,
    string? Metadata);

public record ResolveApprovalRequestPayload(string? Notes);
