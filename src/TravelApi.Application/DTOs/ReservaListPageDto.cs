namespace TravelApi.Application.DTOs;

public class ReservaListSummaryDto
{
    public int BudgetCount { get; set; }
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int OperativeCount { get; set; }
    public int ClosedCount { get; set; }

    // Rediseño Fase A+B (2026-05-30): conteos de los dos estados nuevos. Solo se
    // poblan con el flag EnableSoldToSettleStates prendido; con el flag apagado quedan
    // en 0 (no existen filas en esos estados). Campos ADITIVOS: el frontend viejo los
    // ignora sin romperse hasta que la fase de UI los muestre.
    public int SoldCount { get; set; }
    public int ToSettleCount { get; set; }
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
