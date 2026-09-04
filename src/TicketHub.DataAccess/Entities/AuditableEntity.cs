namespace TicketHub.DataAccess.Entities;

/// <summary>
/// The audit columns every table in this project carries.
/// </summary>
/// <remarks>
/// HOW THIS WORKS — read this once and the rest of the project makes sense.
/// <para/>
/// Nobody writes <c>CreatedAt = DateTime.UtcNow</c> anywhere in this codebase. Instead,
/// <c>TicketHubDbContext.SaveChangesAsync()</c> walks its change tracker just before it talks
/// to the database, finds every entry whose entity inherits this class, and fills these in.
/// One place, impossible to forget, impossible to get inconsistent.
/// <para/>
/// It is <c>abstract</c> because nobody should ever instantiate one, and no <c>DbSet</c>
/// points at it — so EF does not make a table for it. The properties simply appear as extra
/// columns on every entity that inherits it.
/// <para/>
/// WHY UTC, ALWAYS. <c>DateTime.Now</c> gives you the server's local time, which changes when
/// the server moves region, and jumps an hour twice a year. Sorting by a timestamp that goes
/// backwards for an hour every October is a genuinely miserable bug to find. Store UTC,
/// convert once at the edge, never in between.
/// </remarks>
public abstract class AuditableEntity
{
    /// <summary>When the row was inserted. UTC. Set by SaveChanges, never by hand.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Who inserted it. Nullable because seeding and background jobs have no logged-in user —
    /// and a nullable column is more honest than pretending user 0 did it.
    /// </summary>
    public int? CreatedById { get; set; }

    /// <summary>Null until the row is first modified. Also set by SaveChanges.</summary>
    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedById { get; set; }

    // ---------------------------------------------------------------------
    // Soft delete
    // ---------------------------------------------------------------------

    /// <summary>
    /// True = the row is deleted as far as the application is concerned, but it is still
    /// physically there.
    /// </summary>
    /// <remarks>
    /// A global query filter in the DbContext appends <c>WHERE IsDeleted = 0</c> to every
    /// query automatically, so a soft-deleted row simply stops appearing. That matters here
    /// because a ticket's history, its comments and its audit trail have legal and practical
    /// value long after someone clicks Delete — and because "restore it" is a support request
    /// you will get, whereas <c>DELETE FROM</c> is forever.
    /// </remarks>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
    public int? DeletedById { get; set; }
}
