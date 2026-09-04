namespace TicketHub.DataAccess.Entities;

/// <summary>
/// How the reporter scored the handling of their ticket. At most one per ticket.
/// </summary>
/// <remarks>
/// A SECOND WORKED EXAMPLE OF 1:1 — different from <c>AgentProfile</c> on purpose.
/// <para/>
/// <c>AgentProfile</c> makes the FK the primary key. Here the entity has its own
/// <see cref="Id"/> and the 1:1 comes from a <b>unique index on <see cref="TicketId"/></b>
/// instead. Both are correct; which you pick depends on whether the row is a natural
/// extension of its parent (profile → same key) or a thing in its own right that happens to
/// be limited to one (rating → own key, unique FK).
/// <para/>
/// What is NOT enough on its own: checking in C# whether a rating already exists. Two
/// simultaneous requests can both pass that check and both insert. The unique index is the
/// only thing the database will refuse to break.
/// </remarks>
public class Rating : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>
    /// 1 to 5. Validated in the DTO, and again by a CHECK constraint in the database.
    /// </summary>
    /// <remarks>
    /// Belt and braces, and worth it: the DTO gives a clean 400 before anything touches the
    /// database, and the constraint keeps the data correct even when a row arrives from a
    /// migration script, a seeder, or someone with SSMS open.
    /// </remarks>
    public int Stars { get; set; }

    public string? Comment { get; set; }

    /// <summary>Unique — this is what makes the relationship 1:1.</summary>
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public int? RatedByUserId { get; set; }
    public ApplicationUser? RatedByUser { get; set; }
}
