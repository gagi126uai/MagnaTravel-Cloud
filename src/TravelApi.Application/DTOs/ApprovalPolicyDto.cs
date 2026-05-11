namespace TravelApi.Application.DTOs;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): policy por RequestType.
/// </summary>
public class ApprovalPolicyDto
{
    public string RequestType { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }
    public int? ExpirationDaysOverride { get; set; }
    public int? CooldownHoursOverride { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
    public string? UpdatedByUserName { get; set; }
}

public record UpdateApprovalPolicyPayload(
    bool RequiresApproval,
    int? ExpirationDaysOverride,
    int? CooldownHoursOverride,
    string? Notes);
