using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): policies de workflow configurables por Admin.
/// El Admin decide desde UI qué acciones requieren aprobación y puede overridear
/// expiración/cooldown por tipo.
///
/// Consumidores (ej. InvoiceService) llaman <see cref="GetAsync"/> para decidir
/// si exigir ApprovalRequest. <see cref="GetEffectiveExpirationDays"/> y
/// <see cref="GetEffectiveCooldownHours"/> aplican el override o fallback al
/// default global.
/// </summary>
public interface IApprovalPolicyService
{
    Task<IReadOnlyList<ApprovalPolicyDto>> GetAllAsync(CancellationToken ct = default);
    Task<ApprovalPolicyDto?> GetAsync(ApprovalRequestType requestType, CancellationToken ct = default);
    Task<ApprovalPolicyDto> UpdateAsync(
        ApprovalRequestType requestType,
        UpdateApprovalPolicyPayload payload,
        string updatedByUserId,
        string? updatedByUserName,
        CancellationToken ct = default);

    /// <summary>
    /// True si el RequestType requiere aprobacion segun la policy. Si no hay
    /// policy persistida, devuelve <paramref name="fallback"/> (default true
    /// para no abrir flujos sin querer).
    /// </summary>
    Task<bool> RequiresApprovalAsync(ApprovalRequestType requestType, bool fallback = true, CancellationToken ct = default);

    /// <summary>
    /// Devuelve override de la policy (si existe) o el default global.
    /// </summary>
    Task<int> GetEffectiveExpirationDaysAsync(ApprovalRequestType requestType, int globalDefault, CancellationToken ct = default);
    Task<int> GetEffectiveCooldownHoursAsync(ApprovalRequestType requestType, int globalDefault, CancellationToken ct = default);
}
