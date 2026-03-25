namespace TravelApi.Application.DTOs;

public class PagedQuery
{
    private static readonly HashSet<int> AllowedPageSizes = new([25, 50, 100]);

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string? SortDir { get; set; }

    public int GetNormalizedPage() => Page < 1 ? 1 : Page;

    public int GetNormalizedPageSize() => AllowedPageSizes.Contains(PageSize) ? PageSize : 25;

    public bool IsSortDescending() => string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);
}

public class PagedResponse<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public bool HasPreviousPage { get; init; }
    public bool HasNextPage { get; init; }

    public static PagedResponse<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedResponse<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasPreviousPage = page > 1 && totalPages > 0,
            HasNextPage = totalPages > 0 && page < totalPages
        };
    }
}

public class ReservaListQuery : PagedQuery
{
    public string View { get; set; } = "active";
    public ReservaListQuery()
    {
        SortBy = "startDate";
        SortDir = "asc";
    }
}

public class CustomerListQuery : PagedQuery
{
    public bool IncludeInactive { get; set; }
    public CustomerListQuery()
    {
        SortBy = "fullName";
        SortDir = "asc";
    }
}

public class PaymentsListQuery : PagedQuery
{
    public PaymentsListQuery()
    {
        SortBy = "paidAt";
        SortDir = "desc";
    }
}

public class InvoicesListQuery : PagedQuery
{
    public InvoicesListQuery()
    {
        SortBy = "createdAt";
        SortDir = "desc";
    }
}

public class TreasuryMovementsQuery : PagedQuery
{
    public string Direction { get; set; } = "all";
    public string SourceType { get; set; } = "all";
    public TreasuryMovementsQuery()
    {
        SortBy = "occurredAt";
        SortDir = "desc";
    }
}

public class FinanceHistoryQuery : PagedQuery
{
    public FinanceHistoryQuery()
    {
        SortBy = "occurredAt";
        SortDir = "desc";
    }
}
