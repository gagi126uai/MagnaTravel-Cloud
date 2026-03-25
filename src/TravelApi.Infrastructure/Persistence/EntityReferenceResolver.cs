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

    public Task<TEntity?> FindAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return _dbContext.Set<TEntity>()
            .AsQueryable()
            .FindByPublicIdAsync(publicId, cancellationToken);
    }

    public async Task<int> ResolveRequiredIdAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        var id = await _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicId, cancellationToken);

        if (!id.HasValue)
        {
            throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado.");
        }

        return id.Value;
    }

    public Task<Guid?> ResolvePublicIdAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolvePublicIdAsync(publicId, cancellationToken);
    }

    public Task<Guid?> ResolvePublicIdAsync<TEntity>(int internalId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId
    {
        return _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolvePublicIdAsync(internalId, cancellationToken);
    }
}
