using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;

namespace TravelApi.Infrastructure.Persistence;

public static class PagedQueryExtensions
{
    public static async Task<PagedResponse<T>> ToPagedResponseAsync<T>(
        this IQueryable<T> query,
        PagedQuery request,
        CancellationToken cancellationToken)
    {
        var page = request.GetNormalizedPage();
        var pageSize = request.GetNormalizedPageSize();
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return PagedResponse<T>.Create(items, page, pageSize, totalCount);
    }
}
