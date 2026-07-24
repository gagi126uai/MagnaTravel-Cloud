namespace TravelApi.Application.DTOs;

public class ReservaListSummaryDto
{
    // ADR-020 (2026-06-07): contadores por etapa del ciclo unico. QuotationCount/InManagementCount/
    // LostCount nacen aca; SoldCount (Vendida) murio junto con el estado. ReservedCount sigue siendo
    // el conteo de Confirmadas (la clave de tab cambio de "reserved" a "confirmed" en F3 frontend).
    public int QuotationCount { get; set; }
    public int BudgetCount { get; set; }
    public int InManagementCount { get; set; }
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int OperativeCount { get; set; }
    // ADR-036 (2026-06-21, prepago puro): ToSettleCount se ELIMINO (el estado "A liquidar" murio). El
    // frontend que consumia este campo (chip/tab "A liquidar") debe quitarlo en el lote de UI.
    // FIX #37/#38 (Tanda 3, 2026-07-23): ClosedCount pasa a ser SOLO reservas Closed ("Finalizadas").
    // Antes tambien sumaba Cancelled y Archived, y no coincidia con lo que mostraba la pestaña.
    public int ClosedCount { get; set; }
    // Pestaña nueva "Anuladas": Cancelled + PendingOperatorRefund (EstadoReserva.VoidedStatuses).
    public int CancelledCount { get; set; }
    // Pestaña "Archivadas" (soft-delete legacy).
    public int ArchivedCount { get; set; }
    public int LostCount { get; set; }
    public decimal TotalSaleActive { get; set; }
    public decimal TotalCostActive { get; set; }
    public decimal TotalPendingBalance { get; set; }
    public decimal GrossProfit { get; set; }
}

public class ReservaListPageDto : PagedResponse<ReservaListDto>
{
    public ReservaListSummaryDto Summary { get; init; } = new();

    public static ReservaListPageDto Create(
        IReadOnlyList<ReservaListDto> items,
        int page,
        int pageSize,
        int totalCount,
        ReservaListSummaryDto summary)
    {
        var basePage = PagedResponse<ReservaListDto>.Create(items, page, pageSize, totalCount);

        return new ReservaListPageDto
        {
            Items = basePage.Items,
            Page = basePage.Page,
            PageSize = basePage.PageSize,
            TotalCount = basePage.TotalCount,
            TotalPages = basePage.TotalPages,
            HasPreviousPage = basePage.HasPreviousPage,
            HasNextPage = basePage.HasNextPage,
            Summary = summary
        };
    }
}
