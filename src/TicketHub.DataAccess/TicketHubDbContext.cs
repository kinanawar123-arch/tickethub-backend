using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess;

/// <summary>
/// The one class that knows how our C# objects map onto tables — and the single place where
/// auditing and soft delete are enforced.
/// </summary>
/// <remarks>
/// WHAT A DbContext ACTUALLY IS. Two things at once:
/// <list type="number">
/// <item>A <b>unit of work</b>. You make changes to tracked objects, then call
///       <see cref="SaveChangesAsync(CancellationToken)"/> once and everything goes in a
///       single transaction. Either all of it lands or none of it does.</item>
/// <item>A <b>set of repositories</b>. Each <c>DbSet&lt;T&gt;</c> is a queryable collection
///       of one entity type.</item>
/// </list>
/// It is deliberately short-lived — one per HTTP request, registered as Scoped. It is not
/// thread-safe, and a long-lived one accumulates tracked entities until it is both a memory
/// leak and a source of very confusing stale data.
/// <para/>
/// It inherits <see cref="IdentityDbContext{TUser,TRole,TKey}"/>, which brings in
/// <c>AspNetUsers</c>, <c>AspNetRoles</c>, <c>AspNetUserRoles</c>, <c>AspNetUserClaims</c> and
/// friends. That is why <c>base.OnModelCreating(builder)</c> must be the first line of
/// <see cref="OnModelCreating"/> — skip it and Identity's own mappings never get applied.
/// </remarks>
public class TicketHubDbContext
    : IdentityDbContext<ApplicationUser, ApplicationRole, int>
{
    /// <summary>
    /// Who is making the current request. Injected, so this class never touches HTTP.
    /// </summary>
    /// <remarks>
    /// Nullable because migrations and design-time tooling construct a DbContext with no
    /// request in sight. Every use below is null-safe for that reason.
    /// </remarks>
    private readonly ICurrentUser? _currentUser;

    public TicketHubDbContext(
        DbContextOptions<TicketHubDbContext> options,
        ICurrentUser? currentUser = null)
        : base(options)
    {
        _currentUser = currentUser;
    }

    // -------------------------------------------------------------------------
    // DbSets — one per aggregate we query directly.
    // Child entities reachable only through a parent (AgentProfile, ChatMessage)
    // still get a DbSet here, because sooner or later you want to query them directly.
    // -------------------------------------------------------------------------

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketHistory> TicketHistories => Set<TicketHistory>();
    public DbSet<Rating> Ratings => Set<Rating>();

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// Where the model is built. Runs once per application lifetime, not once per request.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // MUST be first. Identity maps its own eight tables in here.
        base.OnModelCreating(builder);

        // Pick up every IEntityTypeConfiguration<T> in this assembly.
        //
        // The alternative is a thousand-line OnModelCreating that nobody wants to open.
        // One small file per entity, discovered automatically — add a new configuration
        // class and it is wired up with no registration step to forget.
        builder.ApplyConfigurationsFromAssembly(typeof(TicketHubDbContext).Assembly);

        // Rename Identity's tables to something readable.
        // Purely cosmetic, and the kind of thing you must do in the FIRST migration —
        // renaming tables later is a migration nobody enjoys reviewing.
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<ApplicationRole>().ToTable("Roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<int>>().ToTable("UserRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<int>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<int>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<int>>().ToTable("RoleClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<int>>().ToTable("UserTokens");

        ApplySoftDeleteQueryFilters(builder);
        ApplyDecimalPrecision(builder);
    }

    /// <summary>
    /// Adds <c>WHERE IsDeleted = 0</c> to every query against every soft-deletable entity.
    /// </summary>
    /// <remarks>
    /// A <b>global query filter</b> is applied by EF to every LINQ query for that type,
    /// including when it appears inside an <c>Include</c>. Which means a developer who forgets
    /// about soft delete still gets the right answer — the safest kind of rule.
    /// <para/>
    /// TWO THINGS THAT WILL BITE YOU, so read them now rather than at 11pm:
    /// <list type="bullet">
    /// <item>To deliberately include deleted rows, use <c>.IgnoreQueryFilters()</c>. There is
    ///       no way to bypass it for one <c>Where</c> clause.</item>
    /// <item>If <c>Ticket</c> is filtered and <c>TicketComment</c> is not, EF warns about a
    ///       required navigation to a filtered entity — a comment whose ticket is invisible
    ///       is a broken object. That is why every child here is filtered too.</item>
    /// </list>
    /// This is done in a loop over the model instead of one line per entity, so a new
    /// auditable entity gets the filter automatically and cannot be forgotten.
    /// </remarks>
    private static void ApplySoftDeleteQueryFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            // Build "e => !e.IsDeleted" as an expression tree, because we do not know the
            // entity type at compile time here.
            var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "e");
            var property = System.Linq.Expressions.Expression.Property(
                parameter, nameof(AuditableEntity.IsDeleted));
            var notDeleted = System.Linq.Expressions.Expression.Not(property);
            var lambda = System.Linq.Expressions.Expression.Lambda(notDeleted, parameter);

            builder.Entity(entityType.ClrType).HasQueryFilter(lambda);
        }
    }

    /// <summary>
    /// Pins the precision of every decimal column.
    /// </summary>
    /// <remarks>
    /// Left alone, EF maps <c>decimal</c> to SQL Server's default <c>decimal(18,2)</c> and
    /// logs a warning. For a latitude that means everything after two decimal places is
    /// silently thrown away — roughly a kilometre of error, which is a lot when you are trying
    /// to find one pothole. Always state the precision.
    /// </remarks>
    private static void ApplyDecimalPrecision(ModelBuilder builder)
    {
        builder.Entity<Ticket>().Property(t => t.Latitude).HasPrecision(9, 6);
        builder.Entity<Ticket>().Property(t => t.Longitude).HasPrecision(9, 6);
    }

    // =========================================================================
    // SaveChanges — where auditing happens
    // =========================================================================

    /// <summary>
    /// Stamps audit columns, turns hard deletes into soft ones, writes the audit log,
    /// and then saves. All of it in one transaction.
    /// </summary>
    /// <remarks>
    /// THIS IS THE MOST IMPORTANT METHOD IN THE DATA LAYER. Because it is here, no developer
    /// ever writes <c>CreatedAt = DateTime.UtcNow</c>, and no developer can forget to.
    /// <para/>
    /// The order matters: we stamp first (so the audit log records the final values), then
    /// collect the audit entries, then save.
    /// </remarks>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser?.UserId;
        var userName = _currentUser?.DisplayName ?? _currentUser?.Email;

        StampAuditColumns(now, userId);
        var auditEntries = CollectAuditEntries(now, userId, userName);

        if (auditEntries.Count > 0)
        {
            AuditLogs.AddRange(auditEntries);
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The synchronous door into the same behaviour, so nothing bypasses auditing.</summary>
    public override int SaveChanges()
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser?.UserId;
        var userName = _currentUser?.DisplayName ?? _currentUser?.Email;

        StampAuditColumns(now, userId);
        var auditEntries = CollectAuditEntries(now, userId, userName);

        if (auditEntries.Count > 0)
        {
            AuditLogs.AddRange(auditEntries);
        }

        return base.SaveChanges();
    }

    /// <summary>
    /// Fills in Created*/Updated*/Deleted* and converts Delete into a soft delete.
    /// </summary>
    private void StampAuditColumns(DateTime now, int? userId)
    {
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    // Only stamp when the caller left it alone. The seeder deliberately
                    // back-dates its demo tickets so the data has some history in it, and a
                    // data import has real timestamps of its own worth keeping. Everywhere
                    // else CreatedAt is still default(DateTime) and this fills it in.
                    if (entry.Entity.CreatedAt == default)
                    {
                        entry.Entity.CreatedAt = now;
                    }

                    entry.Entity.CreatedById ??= userId;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedById = userId;

                    // Guard against an update accidentally rewriting creation data.
                    // Someone maps a DTO onto a detached entity, CreatedAt is default(DateTime),
                    // and the row's history is quietly destroyed. One line stops it.
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(AuditableEntity.CreatedById)).IsModified = false;
                    break;

                case EntityState.Deleted:
                    // THE SOFT-DELETE SWITCH.
                    // Nothing is ever physically removed: we flip the entry back to Modified
                    // and set the flag instead. The global query filter then makes the row
                    // disappear from every query without any caller changing.
                    //
                    // Note this catches CASCADE deletes too — when EF marks a ticket's
                    // comments Deleted because their ticket went, they land here and are
                    // soft-deleted as well. Exactly what we want.
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = now;
                    entry.Entity.DeletedById = userId;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedById = userId;
                    break;
            }
        }

        // ApplicationUser does not inherit AuditableEntity (Identity owns that table),
        // so it gets its two timestamps here.
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    /// <summary>
    /// Builds one <see cref="AuditLog"/> row per changed entity, recording only the properties
    /// that actually changed.
    /// </summary>
    private List<AuditLog> CollectAuditEntries(DateTime now, int? userId, string? userName)
    {
        var logs = new List<AuditLog>();

        foreach (var entry in ChangeTracker.Entries())
        {
            // Do not audit the audit log. That way lies infinite recursion.
            if (entry.Entity is AuditLog or RefreshToken)
            {
                continue;
            }

            if (entry.Entity is not AuditableEntity auditable)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            // A soft delete arrives here as Modified with IsDeleted just turned on.
            // Report it as a Deleted action, because that is what a human meant by it.
            var action = entry.State == EntityState.Added
                ? AuditAction.Created
                : auditable.IsDeleted && entry.Property(nameof(AuditableEntity.IsDeleted)).IsModified
                    ? AuditAction.Deleted
                    : AuditAction.Updated;

            logs.Add(new AuditLog
            {
                EntityName = entry.Metadata.ClrType.Name,
                EntityId = GetPrimaryKeyAsString(entry),
                Action = action,
                Changes = SerialiseChanges(entry, action),
                UserId = userId,
                UserName = userName,
                OccurredAt = now
            });
        }

        return logs;
    }

    /// <summary>
    /// Reads the primary key off a tracked entry.
    /// </summary>
    /// <remarks>
    /// For an INSERT the key is still 0 at this point — the database assigns it during
    /// SaveChanges. Recording "0" is honest but useless, so we write "(pending)". Getting the
    /// real value would mean saving twice; for a training project the trade is not worth it.
    /// A production system that needs it would use client-generated GUID keys instead.
    /// </remarks>
    private static string GetPrimaryKeyAsString(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return "(none)";
        }

        var values = key.Properties
            .Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "null")
            .ToArray();

        var joined = string.Join(",", values);
        return entry.State == EntityState.Added && joined is "0" ? "(pending)" : joined;
    }

    /// <summary>
    /// JSON of what changed: <c>{"Status":{"old":"Open","new":"InProgress"}}</c>.
    /// </summary>
    private static string? SerialiseChanges(EntityEntry entry, AuditAction action)
    {
        var changes = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            // Never write a password hash or a security stamp into a log table.
            // Logs get copied to laptops, pasted into tickets, and shipped to third-party
            // log aggregators. Secrets do not belong in any of those places.
            if (property.Metadata.Name is "PasswordHash" or "SecurityStamp" or "Token" or "ConcurrencyStamp")
            {
                continue;
            }

            if (action == AuditAction.Created)
            {
                if (property.CurrentValue is not null)
                {
                    changes[property.Metadata.Name] = property.CurrentValue.ToString();
                }
            }
            else if (property.IsModified)
            {
                changes[property.Metadata.Name] = new
                {
                    old = property.OriginalValue?.ToString(),
                    @new = property.CurrentValue?.ToString()
                };
            }
        }

        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }
}
