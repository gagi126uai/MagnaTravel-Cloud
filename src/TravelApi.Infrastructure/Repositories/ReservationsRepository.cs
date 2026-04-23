using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Repositories;

public class ReservationsRepository<T> : IRepository<T> where T : class
{
    private readonly AppDbContext _db;
    private readonly DbSet<T> _dbSet;

    public ReservationsRepository(AppDbContext db)
    {
        _db = db;
        _dbSet = db.Set<T>();
    }

    public virtual async Task<T?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, ct);
    }

    public virtual async Task<T?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, ct);
    }

    public virtual async Task<IEnumerable<T>> ListAllAsync(CancellationToken ct = default)
    {
        return await _dbSet.AsNoTracking().ToListAsync(ct);
    }

    public virtual async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        return await _dbSet.AsNoTracking().Where(predicate).ToListAsync(ct);
    }

    public virtual async Task AddAsync(T entity, CancellationToken ct = default)
    {
        await _dbSet.AddAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
    }

    public virtual async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        _dbSet.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public virtual async Task DeleteAsync(T entity, CancellationToken ct = default)
    {
        _dbSet.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public virtual IQueryable<T> Query()
    {
        return _dbSet.AsQueryable();
    }

    public virtual IQueryable<T> QueryAsNoTracking()
    {
        return _dbSet.AsNoTracking();
    }
}

