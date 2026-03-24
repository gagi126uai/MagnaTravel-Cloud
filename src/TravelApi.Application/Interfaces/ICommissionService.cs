namespace TravelApi.Application.Interfaces;

public interface ICommissionService
{
    Task<IEnumerable<object>> GetAllRulesAsync(CancellationToken cancellationToken);
    Task<object> CreateRuleAsync(CreateCommissionRuleRequest request, CancellationToken cancellationToken);
    Task<object?> UpdateRuleAsync(int id, UpdateCommissionRuleRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteRuleAsync(int id, CancellationToken cancellationToken);
    Task<decimal> CalculateCommissionAsync(int? supplierId, string? serviceType, CancellationToken cancellationToken);
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
