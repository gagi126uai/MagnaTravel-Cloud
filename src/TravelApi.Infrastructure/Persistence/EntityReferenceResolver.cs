using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public class EntityReferenceResolver
{
    private readonly AppDbContext _dbContext;

    public EntityReferenceResolver(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TEntity?> FindAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return _dbContext.Set<TEntity>()
            .AsQueryable()
            .FindByPublicIdOrLegacyIdAsync(publicIdOrLegacyId, cancellationToken);
    }

    public async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        var id = await _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!id.HasValue)
        {
            throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado.");
        }

        return id.Value;
    }

    public Task<Guid?> ResolvePublicIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolvePublicIdAsync(publicIdOrLegacyId, cancellationToken);
    }
}
