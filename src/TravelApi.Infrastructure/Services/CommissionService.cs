using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class CommissionService : ICommissionService
{
    private readonly AppDbContext _dbContext;

    public CommissionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<object>> GetAllRulesAsync(CancellationToken cancellationToken)
    {
        var rules = await _dbContext.CommissionRules
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Supplier != null ? r.Supplier.Name : "")
            .ToListAsync(cancellationToken);

        return rules.Select(r => new
        {
            r.Id,
            SupplierPublicId = r.Supplier != null ? (Guid?)r.Supplier.PublicId : null,
            SupplierName = r.Supplier?.Name,
            r.ServiceType,
            r.CommissionPercent,
            r.Priority,
            r.IsActive,
            r.Description,
            r.CreatedAt
        });
    }

    public async Task<object> CreateRuleAsync(CreateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _dbContext.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, cancellationToken);

            if (!supplierId.HasValue)
                throw new ArgumentException("Proveedor no encontrado.");
        }

        // Verificar si ya existe una regla igual
        var existing = await _dbContext.CommissionRules
            .FirstOrDefaultAsync(r => 
                r.SupplierId == supplierId && 
                r.ServiceType == request.ServiceType &&
                r.IsActive, cancellationToken);

        if (existing != null)
            throw new ArgumentException("Ya existe una regla con ese proveedor y tipo de servicio");

        var rule = new CommissionRule
        {
            SupplierId = supplierId,
            ServiceType = request.ServiceType,
            CommissionPercent = request.CommissionPercent,
            Priority = request.Priority,
            Description = request.Description,
            IsActive = true
        };

        _dbContext.CommissionRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return rule;
    }

    public async Task<object?> UpdateRuleAsync(int id, UpdateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return null;

        rule.CommissionPercent = request.CommissionPercent;
        rule.Description = request.Description;
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<bool> DeleteRuleAsync(int id, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return false;

        _dbContext.CommissionRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<decimal> CalculateCommissionAsync(int? supplierId, string? serviceType, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules
            .Where(r => r.IsActive)
            .Where(r => 
                // Regla exacta (proveedor + servicio)
                (r.SupplierId == supplierId && r.ServiceType == serviceType) ||
                // Solo proveedor
                (r.SupplierId == supplierId && r.ServiceType == null) ||
                // Solo servicio
                (r.SupplierId == null && r.ServiceType == serviceType) ||
                // Default (aplica a todos)
                (r.SupplierId == null && r.ServiceType == null)
            )
            .OrderByDescending(r => r.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (rule == null)
        {
            var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
            return settings?.DefaultCommissionPercent ?? 10m;
        }

        return rule.CommissionPercent;
    }
}
