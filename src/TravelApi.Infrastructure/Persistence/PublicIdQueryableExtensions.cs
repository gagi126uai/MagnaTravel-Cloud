using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public static class PublicIdQueryableExtensions
{
    public static Task<TEntity?> FindByPublicIdOrLegacyIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicIdOrLegacyId, out var publicId))
        {
            return query.FirstOrDefaultAsync(entity => entity.PublicId == publicId, cancellationToken);
        }

        if (int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            return query.FirstOrDefaultAsync(entity => EF.Property<int>(entity, "Id") == legacyId, cancellationToken);
        }

        return Task.FromResult<TEntity?>(null);
    }

    public static async Task<int?> ResolveInternalIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicIdOrLegacyId, out var publicId))
        {
            return await query
                .Where(item => item.PublicId == publicId)
                .Select(item => (int?)EF.Property<int>(item, "Id"))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            return legacyId;
        }

        return null;
    }

    public static async Task<Guid?> ResolvePublicIdAsync<TEntity>(
        this IQueryable<TEntity> query,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        if (Guid.TryParse(publicIdOrLegacyId, out var publicId))
        {
            return publicId;
        }

        if (int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            return await query
                .Where(item => EF.Property<int>(item, "Id") == legacyId)
                .Select(item => (Guid?)item.PublicId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }
}
