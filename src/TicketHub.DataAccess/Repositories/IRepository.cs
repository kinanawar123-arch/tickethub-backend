using System.Linq.Expressions;

namespace TicketHub.DataAccess.Repositories;

/// <summary>
/// The operations every entity needs, so twelve repositories do not each rewrite them.
/// </summary>
/// <remarks>
/// A FAIR QUESTION: EF Core's <c>DbSet&lt;T&gt;</c> already <em>is</em> a repository and
/// <c>DbContext</c> already <em>is</em> a unit of work. So why add a layer on top?
/// <para/>
/// The honest answer is that it is a trade, not a free win:
/// <list type="bullet">
/// <item><b>For</b> — the business layer never sees <c>IQueryable</c>, which means it cannot
///       accidentally leave a query un-materialised and have it execute somewhere surprising.
///       Query logic lives in one findable place instead of being spread across services.
///       And a service becomes testable with a fake repository, no in-memory provider needed.</item>
/// <item><b>Against</b> — it is genuinely another layer, and a badly written generic
///       repository that only exposes <c>GetAll()</c> forces you to pull whole tables into
///       memory. That is worse than no repository at all.</item>
/// </list>
/// The rule that keeps it useful: <b>every filter, sort and page must happen inside the
/// repository</b>, in SQL. The moment a caller has to filter the result in memory, the
/// abstraction has failed and you should add a method instead of working around it.
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>By primary key. Null when it does not exist (or is soft-deleted).</summary>
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Every row matching a predicate. Read-only — the results are not change-tracked.
    /// </summary>
    /// <remarks>
    /// Use for lookups and reference data. Do NOT use it for a list screen: there is no
    /// paging here, and a table with a hundred thousand rows will happily give you all of them.
    /// </remarks>
    Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);

    /// <summary>
    /// EXISTS, not COUNT.
    /// </summary>
    /// <remarks>
    /// <c>AnyAsync</c> becomes SQL <c>EXISTS</c>, which stops at the first matching row.
    /// <c>CountAsync() &gt; 0</c> counts every one of them and then throws the number away.
    /// On a big table that is the difference between reading one row and reading a million.
    /// </remarks>
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);

    /// <summary>
    /// Stages an insert. Nothing reaches the database until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/>.
    /// </summary>
    Task AddAsync(T entity, CancellationToken ct = default);

    void AddRange(IEnumerable<T> entities);

    /// <summary>
    /// Marks a detached entity as modified.
    /// </summary>
    /// <remarks>
    /// You usually do NOT need this. If you loaded the entity through this repository it is
    /// already tracked, and changing a property is enough — EF notices on its own and writes
    /// only the columns that actually changed. Calling Update on a tracked entity marks
    /// <em>every</em> column modified, producing a wider UPDATE than necessary.
    /// </remarks>
    void Update(T entity);

    /// <summary>
    /// Marks it deleted. In this project SaveChanges turns that into a soft delete —
    /// see <c>TicketHubDbContext.StampAuditColumns</c>.
    /// </summary>
    void Remove(T entity);
}

/// <summary>
/// The transaction boundary, and the place you get repositories from.
/// </summary>
/// <remarks>
/// WHY NOT LET EACH REPOSITORY SAVE ITSELF?
/// Because a single business operation touches several of them. Resolving a ticket updates
/// the ticket, writes a history row, and inserts a notification. If each repository saved
/// independently you would have three transactions, and a failure in the third leaves the
/// database in a state that never should have existed.
/// <para/>
/// One <c>SaveChangesAsync</c> at the end of the operation means all of it lands or none of
/// it does. That is the whole idea, and it is why services call
/// <c>_unitOfWork.SaveChangesAsync()</c> exactly once, at the end.
/// </remarks>
public interface IUnitOfWork
{
    ITicketRepository Tickets { get; }
    ICommentRepository Comments { get; }
    IDepartmentRepository Departments { get; }
    ICategoryRepository Categories { get; }
    IAgentRepository Agents { get; }
    INotificationRepository Notifications { get; }
    IChatRepository Chat { get; }
    IReportRepository Reports { get; }

    /// <summary>Generic access for the entities that need no special queries.</summary>
    IRepository<TEntity> Repository<TEntity>() where TEntity : class;

    /// <summary>Writes everything staged so far, in one transaction. Returns rows affected.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// An explicit transaction, for the rare operation that must call SaveChanges more than
    /// once — e.g. when you need the id the database generated before you can write the
    /// dependent rows.
    /// </summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default);

    Task CommitTransactionAsync(CancellationToken ct = default);
}
