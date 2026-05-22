using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// B1.15 Fase B' (2026-05-11): workflow generico de aprobaciones.
///
/// Politica de idempotencia (estado-basada — alineado con InvoiceService.Retry/
/// EnqueueAnnulmentAsync):
///  - CreateAsync: si ya hay Pending para misma combo, devuelve la existente
///    (no crea duplicado). Doble click no rompe.
///  - ApproveAsync/RejectAsync: si ya esta en el estado terminal correcto,
///    no-op idempotente. Si esta en otro estado terminal, throws con codigo.
///
/// El consumo de la aprobacion es responsabilidad del handler de cada accion
/// (ej. InvoiceService.AnnulInvoice llama FindActiveApprovedAsync antes de
/// procesar, y MarkConsumedAsync al final). Eso desacopla este service de
/// los flujos especificos.
///
/// <para>FC1.3.4 (ADR-009 §2.7, 2026-05-21): este service ahora dispara
/// callbacks "post-resolucion" cuando el ApprovalRequest resuelto es del tipo
/// <c>PartialCreditNoteApproval</c>. Los callbacks viven en
/// <see cref="IPartialCreditNoteApprovalBridge"/> (implementada por
/// <c>BookingCancellationService</c>) y se invocan DESPUES del
/// <c>SaveChangesAsync</c> que persiste la transicion a Approved/Rejected. Si
/// el callback falla, la AR queda en su estado terminal y se loguea el error
/// (no rollback) — el bridge es idempotente y el job de reconciliacion
/// FC1.3.6b levanta BCs huerfanos. La interface es OPCIONAL en el ctor para
/// no romper tests legacy y para que ApprovalRequestService pueda seguir
/// operando standalone cuando no hay BC asociado.</para>
/// </summary>
public class ApprovalRequestService : IApprovalRequestService
{
    private readonly AppDbContext _context;
    private readonly IOperationalFinanceSettingsService _settingsService;
    // B1.15 Fase B'' (2026-05-11): opcional para no romper unit tests del ctor previo.
    private readonly IApprovalPolicyService? _policyService;
    // FC1.3.4 (2026-05-21): callback hacia BookingCancellationService cuando el
    // approval resuelto es de tipo PartialCreditNoteApproval. Resolucion lazy
    // via IServiceProvider para romper el ciclo DI:
    //   ApprovalRequestService -> IPartialCreditNoteApprovalBridge
    //                          -> BookingCancellationService
    //                          -> IApprovalRequestService  (CICLO)
    // Si inyectaramos la interface directamente en el ctor, el container
    // detectaria "scoped circular dependency" al startup y abortaria. Con
    // IServiceProvider la resolucion se difiere al momento del callback —
    // para ese entonces el grafo ya esta armado y no hay ciclo en tiempo de
    // construccion. Es opcional para preservar compat con tests legacy del
    // ctor previo y para soportar uso standalone (sin BC en la solucion).
    private readonly IServiceProvider? _serviceProvider;
    // FC1.3.4 (2026-05-21): logger opcional. Opcional porque los tests legacy
    // del ctor instancian sin logger y queremos preservar compat. En produccion
    // siempre lo inyecta el DI container.
    private readonly ILogger<ApprovalRequestService>? _logger;

    public ApprovalRequestService(
        AppDbContext context,
        IOperationalFinanceSettingsService settingsService,
        IApprovalPolicyService? policyService = null,
        IServiceProvider? serviceProvider = null,
        ILogger<ApprovalRequestService>? logger = null)
    {
        _context = context;
        _settingsService = settingsService;
        _policyService = policyService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ApprovalRequestDto> CreateAsync(
        CreateApprovalRequestPayload payload,
        string requestedByUserId,
        string? requestedByUserName,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<ApprovalRequestType>(payload.RequestType, out var requestType))
            throw new ArgumentException($"RequestType invalido: {payload.RequestType}", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.EntityType))
            throw new ArgumentException("EntityType es requerido.", nameof(payload));
        if (payload.EntityId <= 0)
            throw new ArgumentException("EntityId debe ser positivo.", nameof(payload));

        // Idempotencia: si ya hay Pending para misma combo, devolver existente.
        var existingPending = await _context.ApprovalRequests
            .FirstOrDefaultAsync(a =>
                a.RequestType == requestType &&
                a.EntityType == payload.EntityType &&
                a.EntityId == payload.EntityId &&
                a.RequestedByUserId == requestedByUserId &&
                a.Status == ApprovalStatus.Pending, ct);
        if (existingPending is not null)
            return Map(existingPending);

        // Cooldown post-rechazo: si hay Rejected reciente con CooldownUntil > now, bloquear.
        var now = DateTime.UtcNow;
        var blockingRejected = await _context.ApprovalRequests
            .Where(a =>
                a.RequestType == requestType &&
                a.EntityType == payload.EntityType &&
                a.EntityId == payload.EntityId &&
                a.RequestedByUserId == requestedByUserId &&
                a.Status == ApprovalStatus.Rejected &&
                a.CooldownUntil != null &&
                a.CooldownUntil > now)
            .OrderByDescending(a => a.CooldownUntil)
            .FirstOrDefaultAsync(ct);
        if (blockingRejected is not null)
        {
            var remainingMinutes = (int)Math.Ceiling((blockingRejected.CooldownUntil!.Value - now).TotalMinutes);
            throw new InvalidOperationException(
                $"Ya existe una solicitud rechazada reciente. Reintenta en {remainingMinutes} minutos.");
        }

        var settings = await _settingsService.GetEntityAsync(ct);
        // B1.15 Fase B'': override por tipo si hay policy configurada.
        var expirationDays = _policyService is not null
            ? await _policyService.GetEffectiveExpirationDaysAsync(requestType, settings.ApprovalDefaultExpirationDays, ct)
            : settings.ApprovalDefaultExpirationDays;
        var request = new ApprovalRequest
        {
            RequestType = requestType,
            RequestedByUserId = requestedByUserId,
            RequestedByUserName = requestedByUserName,
            RequestedAt = now,
            EntityType = payload.EntityType,
            EntityId = payload.EntityId,
            Reason = payload.Reason?.Trim(),
            Status = ApprovalStatus.Pending,
            ExpiresAt = now.AddDays(Math.Max(1, expirationDays)),
            Metadata = payload.Metadata
        };
        _context.ApprovalRequests.Add(request);
        await _context.SaveChangesAsync(ct);
        return Map(request);
    }

    public async Task<ApprovalRequestDto> ApproveAsync(
        Guid publicId,
        string resolvedByUserId,
        string? resolvedByUserName,
        string? notes,
        CancellationToken ct = default)
    {
        var request = await _context.ApprovalRequests.FirstOrDefaultAsync(a => a.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"ApprovalRequest {publicId} no encontrada.");

        if (request.Status == ApprovalStatus.Approved)
            return Map(request); // Idempotente.

        if (request.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"No se puede aprobar una solicitud en estado {request.Status}.");

        request.Status = ApprovalStatus.Approved;
        request.ResolvedByUserId = resolvedByUserId;
        request.ResolvedByUserName = resolvedByUserName;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolverNotes = notes?.Trim();
        await _context.SaveChangesAsync(ct);

        // FC1.3.4 (ADR-009 §2.7, 2026-05-21): post-commit callback al bridge.
        // Importante: la transicion del approval YA quedo persistida arriba. Si
        // el bridge falla (BC bloqueado, BD caida, etc.), NO hacemos rollback —
        // la AR queda en Approved correctamente y el BC asociado queda en
        // ManualReviewPending "huerfano". El job de reconciliacion FC1.3.6b
        // detecta ese desfase y reaplica el callback (el bridge es idempotente
        // por contrato). Una tx distribuida cross-service seria overkill para
        // un caso de borde infrecuente.
        await InvokePartialCreditNoteApprovedCallbackAsync(request, ct);

        return Map(request);
    }

    public async Task<ApprovalRequestDto> RejectAsync(
        Guid publicId,
        string resolvedByUserId,
        string? resolvedByUserName,
        string? notes,
        CancellationToken ct = default)
    {
        var request = await _context.ApprovalRequests.FirstOrDefaultAsync(a => a.PublicId == publicId, ct)
            ?? throw new KeyNotFoundException($"ApprovalRequest {publicId} no encontrada.");

        if (request.Status == ApprovalStatus.Rejected)
            return Map(request);

        if (request.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"No se puede rechazar una solicitud en estado {request.Status}.");

        var settings = await _settingsService.GetEntityAsync(ct);
        // B1.15 Fase B'': override por tipo si hay policy configurada.
        var cooldownHours = _policyService is not null
            ? await _policyService.GetEffectiveCooldownHoursAsync(request.RequestType, settings.ApprovalRejectionCooldownHours, ct)
            : settings.ApprovalRejectionCooldownHours;
        request.Status = ApprovalStatus.Rejected;
        request.ResolvedByUserId = resolvedByUserId;
        request.ResolvedByUserName = resolvedByUserName;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolverNotes = notes?.Trim();
        request.CooldownUntil = DateTime.UtcNow.AddHours(Math.Max(0, cooldownHours));
        await _context.SaveChangesAsync(ct);

        // FC1.3.4 (ADR-009 §2.7, 2026-05-21): mismo patron que ApproveAsync.
        // Si la AR es PartialCreditNoteApproval, notificar al bridge para que
        // transicione el BC a ManualReviewRejected (auto-reset a Drafted).
        // Si falla, log + no rollback — el job de reconciliacion saneara.
        await InvokePartialCreditNoteRejectedCallbackAsync(request, ct);

        return Map(request);
    }

    public async Task<ApprovalRequest?> FindActiveApprovedAsync(
        ApprovalRequestType requestType,
        string entityType,
        int entityId,
        string requestedByUserId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.ApprovalRequests
            .FirstOrDefaultAsync(a =>
                a.RequestType == requestType &&
                a.EntityType == entityType &&
                a.EntityId == entityId &&
                a.RequestedByUserId == requestedByUserId &&
                a.Status == ApprovalStatus.Approved &&
                a.ExpiresAt > now, ct);
    }

    public async Task MarkConsumedAsync(int approvalRequestId, CancellationToken ct = default)
    {
        var request = await _context.ApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalRequestId, ct);
        if (request is null) return;
        if (request.Status == ApprovalStatus.Consumed) return; // Idempotente.
        if (request.Status != ApprovalStatus.Approved)
            throw new InvalidOperationException($"Solo se puede consumir una aprobacion Approved, esta: {request.Status}.");
        request.Status = ApprovalStatus.Consumed;
        request.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApprovalRequestDto>> GetPendingAsync(CancellationToken ct = default)
    {
        var rows = await _context.ApprovalRequests
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            .OrderBy(a => a.RequestedAt)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ApprovalRequestDto>> GetMyRequestsAsync(string userId, CancellationToken ct = default)
    {
        var rows = await _context.ApprovalRequests
            .AsNoTracking()
            .Where(a => a.RequestedByUserId == userId)
            .OrderByDescending(a => a.RequestedAt)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<ApprovalRequestDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        var request = await _context.ApprovalRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.PublicId == publicId, ct);
        return request is null ? null : Map(request);
    }

    public async Task<int> ExpireOverdueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var overdue = await _context.ApprovalRequests
            .Where(a => (a.Status == ApprovalStatus.Pending || a.Status == ApprovalStatus.Approved) && a.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var request in overdue)
        {
            request.Status = ApprovalStatus.Expired;
        }
        if (overdue.Count > 0)
            await _context.SaveChangesAsync(ct);
        return overdue.Count;
    }

    /// <summary>
    /// FC1.3.4 (ADR-009 §2.7, 2026-05-21): invoca el callback del bridge si la
    /// AR resuelta es del tipo <c>PartialCreditNoteApproval</c>. Encapsulado
    /// para mantener Approve/RejectAsync legibles y evitar duplicar el try/catch.
    ///
    /// <para><b>Por que no rollback si el bridge falla</b>: la AR ya quedo
    /// commiteada (Approved) — revertirla seria mentir sobre la decision del
    /// admin. El bridge es idempotente por contrato (FC1.3.2), asi que el job
    /// de reconciliacion FC1.3.6b puede reaplicar el callback mas tarde sin
    /// efectos secundarios.</para>
    /// </summary>
    private async Task InvokePartialCreditNoteApprovedCallbackAsync(
        ApprovalRequest request,
        CancellationToken ct)
    {
        if (request.RequestType != ApprovalRequestType.PartialCreditNoteApproval) return;

        // Resolucion lazy del bridge: si el caller (tests legacy) no inyecto un
        // IServiceProvider, no hay bridge para invocar -> no-op. En produccion
        // siempre lo inyecta el container (registrado en Program.cs FC1.3.2).
        var bridge = _serviceProvider?.GetService<IPartialCreditNoteApprovalBridge>();
        if (bridge is null) return;

        try
        {
            await bridge.OnApprovedAsync(
                request.Id,
                request.ResolvedByUserId ?? string.Empty,
                request.ResolvedByUserName,
                request.ResolverNotes,
                ct);
        }
        catch (Exception ex)
        {
            // Log + return: no rollback. Ver doc del metodo.
            _logger?.LogError(
                ex,
                "PartialCreditNoteApprovalBridge.OnApprovedAsync fallo para ApprovalRequest {ApprovalId} (PublicId {PublicId}). " +
                "Approval queda en Approved, BC asociado puede quedar en ManualReviewPending huerfano. " +
                "El job de reconciliacion FC1.3.6b reaplicara el callback.",
                request.Id,
                request.PublicId);
        }
    }

    /// <summary>
    /// FC1.3.4: simetrico a <see cref="InvokePartialCreditNoteApprovedCallbackAsync"/>
    /// pero para el caso Rejected. Mismo contrato de idempotencia + no rollback.
    /// </summary>
    private async Task InvokePartialCreditNoteRejectedCallbackAsync(
        ApprovalRequest request,
        CancellationToken ct)
    {
        if (request.RequestType != ApprovalRequestType.PartialCreditNoteApproval) return;

        var bridge = _serviceProvider?.GetService<IPartialCreditNoteApprovalBridge>();
        if (bridge is null) return;

        try
        {
            await bridge.OnRejectedAsync(
                request.Id,
                request.ResolvedByUserId ?? string.Empty,
                request.ResolvedByUserName,
                request.ResolverNotes,
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PartialCreditNoteApprovalBridge.OnRejectedAsync fallo para ApprovalRequest {ApprovalId} (PublicId {PublicId}). " +
                "Approval queda en Rejected, BC asociado puede quedar en ManualReviewPending huerfano. " +
                "El job de reconciliacion FC1.3.6b reaplicara el callback.",
                request.Id,
                request.PublicId);
        }
    }

    private static ApprovalRequestDto Map(ApprovalRequest a) => new()
    {
        PublicId = a.PublicId,
        RequestType = a.RequestType.ToString(),
        RequestedByUserId = a.RequestedByUserId,
        RequestedByUserName = a.RequestedByUserName,
        RequestedAt = a.RequestedAt,
        EntityType = a.EntityType,
        EntityId = a.EntityId,
        Reason = a.Reason,
        Status = a.Status.ToString(),
        ResolvedByUserId = a.ResolvedByUserId,
        ResolvedByUserName = a.ResolvedByUserName,
        ResolvedAt = a.ResolvedAt,
        ResolverNotes = a.ResolverNotes,
        ExpiresAt = a.ExpiresAt,
        ConsumedAt = a.ConsumedAt,
        CooldownUntil = a.CooldownUntil,
        Metadata = a.Metadata
    };
}
