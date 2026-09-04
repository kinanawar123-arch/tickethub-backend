using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace TicketHub.DataAccess.Repositories;

/// <summary>The default <see cref="IRepository{T}"/>, backed by a <see cref="DbSet{TEntity}"/>.</summary>
public class Repository<T> : IRepository<T> where T : class
{
    protected readonly TicketHubDbContext Db;
    protected readonly DbSet<T> Set;

    public Repository(TicketHubDbContext db)
    {
        Db = db;
        Set = db.Set<T>();
    }

    public virtual async Task<T?> GetByIdAsync(int id, CancellationToken ct = default)
        // FindAsync, not FirstOrDefaultAsync(x => x.Id == id): if the entity is already
        // tracked by this DbContext, FindAsync returns it from memory without going to the
        // database at all. In a request that loads the same row twice — very common — that
        // is one round trip saved for free.
        => await Set.FindAsync(new object?[] { id }, ct);

    public virtual async Task<IReadOnlyList<T>> ListAsync(
        Expression<Func<T, bool>>? predicate = null,
        CancellationToken ct = default)
    {
        // AsNoTracking: we are not going to modify these, so skip building a change-tracking
        // snapshot of every row. Less memory, less CPU, and no chance of an accidental save.
        IQueryable<T> query = Set.AsNoTracking();

        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        return await query.ToListAsync(ct);
    }

    public virtual Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Set.AsNoTracking().AnyAsync(predicate, ct);

    public virtual Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => predicate is null
            ? Set.AsNoTracking().CountAsync(ct)
            : Set.AsNoTracking().CountAsync(predicate, ct);

    public virtual async Task AddAsync(T entity, CancellationToken ct = default)
        => await Set.AddAsync(entity, ct);

    public virtual void AddRange(IEnumerable<T> entities) => Set.AddRange(entities);

    public virtual void Update(T entity) => Set.Update(entity);

    public virtual void Remove(T entity) => Set.Remove(entity);
}
