namespace TravelApi.Application.DTOs;

public class ReservaListSummaryDto
{
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int OperativeCount { get; set; }
    public int ClosedCount { get; set; }
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
