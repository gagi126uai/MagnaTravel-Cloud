using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ITreasuryService
{
    Task<TreasurySummaryDto> GetSummaryAsync(CancellationToken cancellationToken);
    Task<CashSummaryDto> GetCashSummaryAsync(CancellationToken cancellationToken);
    Task<PagedResponse<CashMovementDto>> GetMovementsAsync(TreasuryMovementsQuery query, CancellationToken cancellationToken);
    Task<ManualCashMovementDto> CreateManualMovementAsync(UpsertManualCashMovementRequest request, string createdBy, CancellationToken cancellationToken);
    Task<ManualCashMovementDto> UpdateManualMovementAsync(int id, UpsertManualCashMovementRequest request, CancellationToken cancellationToken);
    Task DeleteManualMovementAsync(int id, CancellationToken cancellationToken);
}
