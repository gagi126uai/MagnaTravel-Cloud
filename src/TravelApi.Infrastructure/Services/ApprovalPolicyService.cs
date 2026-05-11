using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): CRUD basico sobre ApprovalPolicies.
///
/// Sin cache: la tabla es chica (6-12 filas), se consulta por indice unique en
/// RequestType, y los updates son raros (Admin edita desde UI). Si se vuelve
/// cuello de botella se puede agregar IMemoryCache con invalidacion en Update.
/// </summary>
public class ApprovalPolicyService : IApprovalPolicyService
{
    private readonly AppDbContext _context;

    public ApprovalPolicyService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ApprovalPolicyDto>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _context.ApprovalPolicies.AsNoTracking().ToListAsync(ct);
        return rows.Select(Map).OrderBy(p => p.RequestType).ToList();
    }

    public async Task<ApprovalPolicyDto?> GetAsync(ApprovalRequestType requestType, CancellationToken ct = default)
    {
        var name = requestType.ToString();
        var policy = await _context.ApprovalPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RequestType == name, ct);
        return policy is null ? null : Map(policy);
    }

    public async Task<ApprovalPolicyDto> UpdateAsync(
        ApprovalRequestType requestType,
        UpdateApprovalPolicyPayload payload,
        string updatedByUserId,
        string? updatedByUserName,
        CancellationToken ct = default)
    {
        if (payload.ExpirationDaysOverride is < 1 or > 365)
            throw new ArgumentException("ExpirationDaysOverride debe estar entre 1 y 365 dias o ser null.");
        if (payload.CooldownHoursOverride is < 0 or > 720)
            throw new ArgumentException("CooldownHoursOverride debe estar entre 0 y 720 horas o ser null.");

        var name = requestType.ToString();
        var policy = await _context.ApprovalPolicies
            .FirstOrDefaultAsync(p => p.RequestType == name, ct);

        if (policy is null)
        {
            // Upsert: si no esta seedeada (caso degenerado), la creamos ahora.
            policy = new ApprovalPolicy { RequestType = name };
            _context.ApprovalPolicies.Add(policy);
        }

        policy.RequiresApproval = payload.RequiresApproval;
        policy.ExpirationDaysOverride = payload.ExpirationDaysOverride;
        policy.CooldownHoursOverride = payload.CooldownHoursOverride;
        policy.Notes = payload.Notes?.Trim();
        policy.UpdatedAt = DateTime.UtcNow;
        policy.UpdatedByUserId = updatedByUserId;
        policy.UpdatedByUserName = updatedByUserName;
        await _context.SaveChangesAsync(ct);
        return Map(policy);
    }

    public async Task<bool> RequiresApprovalAsync(ApprovalRequestType requestType, bool fallback = true, CancellationToken ct = default)
    {
        var policy = await GetAsync(requestType, ct);
        return policy?.RequiresApproval ?? fallback;
    }

    public async Task<int> GetEffectiveExpirationDaysAsync(ApprovalRequestType requestType, int globalDefault, CancellationToken ct = default)
    {
        var policy = await GetAsync(requestType, ct);
        return policy?.ExpirationDaysOverride ?? globalDefault;
    }

    public async Task<int> GetEffectiveCooldownHoursAsync(ApprovalRequestType requestType, int globalDefault, CancellationToken ct = default)
    {
        var policy = await GetAsync(requestType, ct);
        return policy?.CooldownHoursOverride ?? globalDefault;
    }

    private static ApprovalPolicyDto Map(ApprovalPolicy p) => new()
    {
        RequestType = p.RequestType,
        RequiresApproval = p.RequiresApproval,
        ExpirationDaysOverride = p.ExpirationDaysOverride,
        CooldownHoursOverride = p.CooldownHoursOverride,
        Notes = p.Notes,
        UpdatedAt = p.UpdatedAt,
        UpdatedByUserId = p.UpdatedByUserId,
        UpdatedByUserName = p.UpdatedByUserName,
    };
}
