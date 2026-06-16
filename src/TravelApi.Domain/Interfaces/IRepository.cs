using System.Linq.Expressions;

namespace TravelApi.Domain.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<T?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<T>> ListAllAsync(CancellationToken ct = default);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    
    Task AddAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Marca una entidad como "para insertar" en el contexto PERO no guarda (no hace SaveChanges).
    /// Sirve cuando el alta tiene que entrar en la MISMA transaccion que otras mutaciones del caller:
    /// el caller cierra todo con un unico SaveChanges y EF agrupa el alta con el resto = atomico.
    /// A diferencia de <see cref="AddAsync"/> (que guarda inmediatamente), esto NO crea una
    /// transaccion separada.
    /// </summary>
    void Stage(T entity);

    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(T entity, CancellationToken ct = default);
    
    IQueryable<T> Query();
    IQueryable<T> QueryAsNoTracking();
}
