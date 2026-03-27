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
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }

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
    public string Kind { get; set; } = "all";
    public string? Period { get; set; }
    public string? Customer { get; set; }
    public string? Reservation { get; set; }
    public string? VoucherNumber { get; set; }
    public string? Result { get; set; }

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

public class SupplierListQuery : PagedQuery
{
    public bool IncludeInactive { get; set; }

    public SupplierListQuery()
    {
        SortBy = "name";
        SortDir = "asc";
    }
}

public class SupplierAccountServicesQuery : PagedQuery
{
    public string? Type { get; set; }

    public SupplierAccountServicesQuery()
    {
        SortBy = "date";
        SortDir = "desc";
    }
}

public class SupplierAccountPaymentsQuery : PagedQuery
{
    public SupplierAccountPaymentsQuery()
    {
        SortBy = "paidAt";
        SortDir = "desc";
    }
}

public class CollectionWorklistQuery : PagedQuery
{
    public string Urgency { get; set; } = "all";

    public CollectionWorklistQuery()
    {
        SortBy = "startDate";
        SortDir = "asc";
    }
}

public class InvoicingWorklistQuery : PagedQuery
{
    public string Status { get; set; } = "ready";
    public string? Customer { get; set; }
    public string? Reservation { get; set; }

    public InvoicingWorklistQuery()
    {
        SortBy = "startDate";
        SortDir = "asc";
    }
}

public class LeadListQuery : PagedQuery
{
    public string View { get; set; } = "active";
    public string? Status { get; set; }
    public string? Source { get; set; }

    public LeadListQuery()
    {
        SortBy = "createdAt";
        SortDir = "desc";
    }
}

public class RateListQuery : PagedQuery
{
    public string? SupplierId { get; set; }
    public string? ServiceType { get; set; }
    public bool ActiveOnly { get; set; }

    public RateListQuery()
    {
        SortBy = "productName";
        SortDir = "asc";
    }
}

public class HotelRateGroupsQuery : PagedQuery
{
    public string? SupplierId { get; set; }
    public bool ActiveOnly { get; set; }

    public HotelRateGroupsQuery()
    {
        SortBy = "hotelName";
        SortDir = "asc";
    }
}

public class RateGroupsQuery : PagedQuery
{
    public string? SupplierId { get; set; }
    public string? ServiceType { get; set; }
    public bool ActiveOnly { get; set; }

    public RateGroupsQuery()
    {
        SortBy = "groupName";
        SortDir = "asc";
    }
}

public class RateSummaryQuery
{
    public string? Search { get; set; }
    public string? ServiceType { get; set; }
    public string? SupplierId { get; set; }
    public bool ActiveOnly { get; set; }
}
