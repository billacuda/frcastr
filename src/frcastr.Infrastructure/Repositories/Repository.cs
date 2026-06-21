using System.Linq.Expressions;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Repositories;

public class Repository<T>(ApplicationDbContext dbContext) : IRepository<T> where T : class
{
    public async Task<T?> GetByIdAsync(object id, CancellationToken ct = default)
        => await dbContext.Set<T>().FindAsync(new[] { id }, ct);

    public async Task<IEnumerable<T>> ListAsync(CancellationToken ct = default)
        => await dbContext.Set<T>().ToListAsync(ct);

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await dbContext.Set<T>().Where(predicate).ToListAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await dbContext.Set<T>().AddAsync(entity, ct);

    public void Update(T entity) => dbContext.Set<T>().Update(entity);
    public void Remove(T entity) => dbContext.Set<T>().Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => dbContext.SaveChangesAsync(ct);
}
