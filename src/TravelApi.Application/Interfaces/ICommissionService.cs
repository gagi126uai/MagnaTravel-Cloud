using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ICommissionService
{
    Task<IEnumerable<object>> GetAllRulesAsync(CancellationToken cancellationToken);
    Task<object> CreateRuleAsync(CreateCommissionRuleRequest request, CancellationToken cancellationToken);
    Task<object?> UpdateRuleAsync(int id, UpdateCommissionRuleRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteRuleAsync(int id, CancellationToken cancellationToken);
    Task<decimal> CalculateCommissionAsync(int? supplierId, string? serviceType, CancellationToken cancellationToken);

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1): lista las comisiones de vendedor devengadas, filtrables por
    /// vendedor / periodo / estado. Dato sensible (tipo costo): el controller lo gatea con
    /// <c>cobranzas.see_cost</c>.
    /// </summary>
    Task<PagedResponse<CommissionAccrualDto>> GetAccrualsAsync(CommissionAccrualsQuery query, CancellationToken cancellationToken);
}

public record CreateCommissionRuleRequest(
    string? SupplierId,
    string? ServiceType,
    decimal CommissionPercent,
    int Priority,
    string? Description
);

public record UpdateCommissionRuleRequest(
    decimal CommissionPercent,
    int Priority,
    string? Description,
    bool IsActive
);
