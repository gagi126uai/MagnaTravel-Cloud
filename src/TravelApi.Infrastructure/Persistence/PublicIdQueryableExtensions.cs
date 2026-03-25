using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public static class PublicIdQueryableExtensions
{
    public static Task<TEntity?> FindByPublicIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicId, out var parsedPublicId))
        {
            return query.FirstOrDefaultAsync(entity => entity.PublicId == parsedPublicId, cancellationToken);
        }

        return Task.FromResult<TEntity?>(null);
    }

    public static async Task<int?> ResolveInternalIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicId, out var parsedPublicId))
        {
            return await query
                .Where(item => item.PublicId == parsedPublicId)
                .Select(item => (int?)EF.Property<int>(item, "Id"))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    public static async Task<Guid?> ResolvePublicIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicId, out var parsedPublicId))
        {
            return parsedPublicId;
        }

        return null;
    }

    public static async Task<Guid?> ResolvePublicIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        int internalId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return await query
            .Where(item => EF.Property<int>(item, "Id") == internalId)
            .Select(item => (Guid?)item.PublicId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
