using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Storage;

namespace TicketHub.DataAccess.Repositories;

/// <summary>
/// One per request. Hands out repositories that all share the same
/// <see cref="TicketHubDbContext"/>, so a single <see cref="SaveChangesAsync"/> commits
/// everything they staged.
/// </summary>
/// <remarks>
/// The shared DbContext is the entire point. If each repository had its own, they would each
/// have their own change tracker and their own transaction, and "save the ticket and its
/// history row together" would stop being possible.
/// </remarks>
public class UnitOfWork : IUnitOfWork
{
    private readonly TicketHubDbContext _db;
    private readonly ConcurrentDictionary<Type, object> _genericRepositories = new();
    private IDbContextTransaction? _transaction;

    public UnitOfWork(TicketHubDbContext db)
    {
        _db = db;

        // Built eagerly: they are trivially cheap (one field assignment each) and it keeps
        // the class readable. If a repository ever becomes expensive to construct, make it lazy.
        Tickets = new TicketRepository(db);
        Comments = new CommentRepository(db);
        Departments = new DepartmentRepository(db);
        Categories = new CategoryRepository(db);
        Agents = new AgentRepository(db);
        Notifications = new NotificationRepository(db);
        Chat = new ChatRepository(db);
        Reports = new ReportRepository(db);
    }

    public ITicketRepository Tickets { get; }
    public ICommentRepository Comments { get; }
    public IDepartmentRepository Departments { get; }
    public ICategoryRepository Categories { get; }
    public IAgentRepository Agents { get; }
    public INotificationRepository Notifications { get; }
    public IChatRepository Chat { get; }
    public IReportRepository Reports { get; }

    /// <summary>
    /// A plain repository for entities with no special queries — <c>Skill</c>,
    /// <c>RefreshToken</c>, <c>AgentProfile</c>.
    /// </summary>
    /// <remarks>
    /// Cached per type so repeated calls in one request return the same instance rather than
    /// allocating a new object each time.
    /// </remarks>
    public IRepository<TEntity> Repository<TEntity>() where TEntity : class
        => (IRepository<TEntity>)_genericRepositories.GetOrAdd(
            typeof(TEntity),
            _ => new Repository<TEntity>(_db));

    /// <summary>
    /// Commits everything staged. This is where auditing and soft delete run —
    /// see <c>TicketHubDbContext.SaveChangesAsync</c>.
    /// </summary>
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    /// <summary>
    /// Starts an explicit transaction.
    /// </summary>
    /// <remarks>
    /// YOU USUALLY DO NOT NEED THIS. A single <c>SaveChangesAsync</c> is already atomic — EF
    /// wraps it in a transaction for you. Reach for an explicit one only when a business
    /// operation genuinely has to save twice, e.g. because it needs the identity value the
    /// database generated before it can write the rows that reference it.
    /// </remarks>
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        _transaction = await _db.Database.BeginTransactionAsync(ct);
        return _transaction;
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    // No Dispose here on purpose. The DbContext is registered as Scoped, so the DI container
    // owns its lifetime and disposes it at the end of the request. Disposing it ourselves
    // would leave anything else resolved in the same scope holding a dead context.
}
