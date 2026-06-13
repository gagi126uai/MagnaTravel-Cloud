using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ITreasuryService
{
    Task<TreasurySummaryDto> GetSummaryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resumen de arqueo de caja. Si <paramref name="year"/>/<paramref name="month"/> vienen, se acota a ESE
    /// mes calendario [primer dia, primer dia del mes siguiente). Si no vienen (default), usa el mes actual de
    /// UtcNow — comportamiento identico al historico, porque el dashboard lo reusa sin esos argumentos.
    /// </summary>
    Task<CashSummaryDto> GetCashSummaryAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default);

    Task<PagedResponse<CashMovementDto>> GetMovementsAsync(TreasuryMovementsQuery query, CancellationToken cancellationToken);
    Task<ManualCashMovementDto> CreateManualMovementAsync(UpsertManualCashMovementRequest request, string createdBy, CancellationToken cancellationToken);
    Task<ManualCashMovementDto> UpdateManualMovementAsync(int id, UpsertManualCashMovementRequest request, CancellationToken cancellationToken);
    Task DeleteManualMovementAsync(int id, CancellationToken cancellationToken);
}
