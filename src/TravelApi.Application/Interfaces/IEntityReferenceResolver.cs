using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IEntityReferenceResolver
{
    Task<TEntity?> FindAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId;

    Task<int> ResolveRequiredIdAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId;

    Task<Guid?> ResolvePublicIdAsync<TEntity>(string publicId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId;

    Task<Guid?> ResolvePublicIdAsync<TEntity>(int internalId, CancellationToken cancellationToken = default)
        where TEntity : class, IHasPublicId;
}
