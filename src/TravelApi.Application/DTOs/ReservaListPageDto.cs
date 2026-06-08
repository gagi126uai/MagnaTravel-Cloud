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
    public int ToSettleCount { get; set; }
    public int ClosedCount { get; set; }
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
