using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
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
            // ADR-026 (M3 review): desempate estable por Id. Con dos reglas de igual Priority,
            // Postgres no garantiza orden sin ORDER BY secundario -> la regla elegida (y por ende
            // la comision que se persiste como plata) seria no determinista. El mismo desempate va
            // en el espejo en memoria de CommissionAccrualPersister para que el % sea reproducible.
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (rule == null)
        {
            var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
            return settings?.DefaultCommissionPercent ?? 10m;
        }

        return rule.CommissionPercent;
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1): lista paginada de comisiones de vendedor devengadas. Solo
    /// lectura (las filas las escribe <c>CommissionAccrualPersister</c> en el recalculo de plata). Filtra
    /// por vendedor / estado / periodo de devengo. Join a la reserva para mostrar numero + publicId.
    /// </summary>
    public async Task<PagedResponse<CommissionAccrualDto>> GetAccrualsAsync(
        CommissionAccrualsQuery query, CancellationToken cancellationToken)
    {
        // Base: todas las comisiones, con la reserva para exponer su numero/publicId (un solo JOIN, sin N+1).
        var accruals = _dbContext.CommissionAccruals
            .AsNoTracking()
            .Include(accrual => accrual.Reserva)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.SellerUserId))
            accruals = accruals.Where(accrual => accrual.SellerUserId == query.SellerUserId);

        if (!string.IsNullOrWhiteSpace(query.Status))
            accruals = accruals.Where(accrual => accrual.Status == query.Status);

        if (query.From.HasValue)
            accruals = accruals.Where(accrual => accrual.CreatedAt >= query.From.Value);

        if (query.To.HasValue)
            accruals = accruals.Where(accrual => accrual.CreatedAt <= query.To.Value);

        int totalCount = await accruals.CountAsync(cancellationToken);

        int page = query.GetNormalizedPage();
        int pageSize = query.GetNormalizedPageSize();
        bool descending = query.IsSortDescending();

        // Orden: por defecto por fecha de devengo (mas reciente arriba). Tie-break por Id para que la
        // paginacion sea estable.
        accruals = descending
            ? accruals.OrderByDescending(accrual => accrual.CreatedAt).ThenByDescending(accrual => accrual.Id)
            : accruals.OrderBy(accrual => accrual.CreatedAt).ThenBy(accrual => accrual.Id);

        var pageRows = await accruals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(accrual => new CommissionAccrualDto
            {
                PublicId = accrual.PublicId,
                SellerUserId = accrual.SellerUserId,
                SellerName = accrual.SellerName,
                ReservaPublicId = accrual.Reserva != null ? accrual.Reserva.PublicId : Guid.Empty,
                ReservaNumber = accrual.Reserva != null ? accrual.Reserva.NumeroReserva : string.Empty,
                Currency = accrual.Currency,
                Amount = accrual.Amount,
                RatePercent = accrual.RatePercent,
                Status = accrual.Status,
                CreatedAt = accrual.CreatedAt,
                UpdatedAt = accrual.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return PagedResponse<CommissionAccrualDto>.Create(pageRows, page, pageSize, totalCount);
    }
}
