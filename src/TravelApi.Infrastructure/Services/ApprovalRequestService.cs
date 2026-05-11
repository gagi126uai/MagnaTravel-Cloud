using Microsoft.EntityFrameworkCore;
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
/// </summary>
public class ApprovalRequestService : IApprovalRequestService
{
    private readonly AppDbContext _context;
    private readonly IOperationalFinanceSettingsService _settingsService;

    public ApprovalRequestService(AppDbContext context, IOperationalFinanceSettingsService settingsService)
    {
        _context = context;
        _settingsService = settingsService;
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
            ExpiresAt = now.AddDays(Math.Max(1, settings.ApprovalDefaultExpirationDays)),
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
        request.Status = ApprovalStatus.Rejected;
        request.ResolvedByUserId = resolvedByUserId;
        request.ResolvedByUserName = resolvedByUserName;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolverNotes = notes?.Trim();
        request.CooldownUntil = DateTime.UtcNow.AddHours(Math.Max(0, settings.ApprovalRejectionCooldownHours));
        await _context.SaveChangesAsync(ct);
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
