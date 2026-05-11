using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// B1.15 Fase B' (2026-05-11): workflow generico de aprobaciones.
///
/// Single responsibility: gestionar el ciclo de vida del ApprovalRequest.
/// NO ejecuta acciones del dominio — esa responsabilidad queda en cada handler
/// (ej. InvoiceService.AnnulInvoice consume el ApprovalRequest aprobado).
/// </summary>
public interface IApprovalRequestService
{
    /// <summary>
    /// Crea una solicitud Pending. Idempotente:
    ///  - Si ya existe Pending para (RequestType, EntityId, RequestedByUserId): devuelve la existente.
    ///  - Si hay cooldown post-rechazo activo para (RequestType, EntityId, RequestedByUserId):
    ///    tira <see cref="InvalidOperationException"/> con codigo "COOLDOWN_ACTIVE".
    /// </summary>
    Task<ApprovalRequestDto> CreateAsync(
        CreateApprovalRequestPayload payload,
        string requestedByUserId,
        string? requestedByUserName,
        CancellationToken ct = default);

    /// <summary>
    /// Aprueba un ApprovalRequest Pending. Idempotente: si ya esta Approved devuelve el actual.
    /// Throws <see cref="InvalidOperationException"/> si esta Rejected/Consumed/Expired.
    /// </summary>
    Task<ApprovalRequestDto> ApproveAsync(
        Guid publicId,
        string resolvedByUserId,
        string? resolvedByUserName,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Rechaza un ApprovalRequest Pending y setea CooldownUntil. Idempotente
    /// como Approve para Rejected. Throws para los otros estados terminales.
    /// </summary>
    Task<ApprovalRequestDto> RejectAsync(
        Guid publicId,
        string resolvedByUserId,
        string? resolvedByUserName,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Busca ApprovalRequest Approved no consumida ni expirada matcheando
    /// (RequestType, EntityType, EntityId, RequestedByUserId). Devuelve null
    /// si no existe. Usar antes de ejecutar la accion solicitada.
    /// </summary>
    Task<ApprovalRequest?> FindActiveApprovedAsync(
        ApprovalRequestType requestType,
        string entityType,
        int entityId,
        string requestedByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Marca como Consumed luego de que el solicitante ejecuto la accion.
    /// Idempotente (no-op si ya Consumed).
    /// </summary>
    Task MarkConsumedAsync(int approvalRequestId, CancellationToken ct = default);

    Task<IReadOnlyList<ApprovalRequestDto>> GetPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRequestDto>> GetMyRequestsAsync(string userId, CancellationToken ct = default);
    Task<ApprovalRequestDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);

    /// <summary>
    /// Job de mantenimiento: marca Expired las solicitudes Pending/Approved
    /// cuyo <c>ExpiresAt</c> ya paso. Idempotente.
    /// </summary>
    Task<int> ExpireOverdueAsync(CancellationToken ct = default);
}
