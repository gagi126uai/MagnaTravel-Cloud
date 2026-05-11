using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): policy por <see cref="ApprovalRequestType"/>.
/// El Admin decide desde UI qué acciones del sistema requieren aprobación y
/// puede overridear la expiración/cooldown por tipo (en lugar del default
/// global de OperationalFinanceSettings).
///
/// Diseño:
///  - Tabla con una fila por RequestType (unique constraint).
///  - <see cref="RequiresApproval"/>: si false, el flujo bypassa el workflow.
///  - <see cref="ExpirationDaysOverride"/> / <see cref="CooldownHoursOverride"/>:
///    null = usar defaults globales. Permite finetuning.
///  - Audit minima (UpdatedAt + UpdatedByUserId).
/// </summary>
public class ApprovalPolicy
{
    public int Id { get; set; }

    /// <summary>Texto canonico del enum <see cref="ApprovalRequestType"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string RequestType { get; set; } = string.Empty;

    public bool RequiresApproval { get; set; } = true;

    /// <summary>Override por tipo. null = usar OperationalFinanceSettings.ApprovalDefaultExpirationDays.</summary>
    public int? ExpirationDaysOverride { get; set; }

    /// <summary>Override por tipo. null = usar OperationalFinanceSettings.ApprovalRejectionCooldownHours.</summary>
    public int? CooldownHoursOverride { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? UpdatedByUserId { get; set; }

    [MaxLength(200)]
    public string? UpdatedByUserName { get; set; }
}
