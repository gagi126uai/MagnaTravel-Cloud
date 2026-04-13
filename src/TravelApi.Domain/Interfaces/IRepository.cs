using System.Linq.Expressions;

namespace TravelApi.Domain.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<T?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<T>> ListAllAsync(CancellationToken ct = default);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    
    Task AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(T entity, CancellationToken ct = default);
    
    IQueryable<T> Query();
    IQueryable<T> QueryAsNoTracking();
}
