namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A comment on a ticket — the written trail of how it got handled.
/// </summary>
/// <remarks>
/// THIS IS THE CLASSIC 1:N, so it is worth naming the parts out loud.
/// <list type="bullet">
/// <item>The rule in SQL: <b>the foreign key always lives on the "many" side</b>. A comment
///       knows its ticket; a ticket does not store a list of comment ids.</item>
/// <item><see cref="TicketId"/> paired with <see cref="Ticket"/> is what makes this the many
///       side. Delete the <c>TicketId</c> property and EF still creates the column as a
///       shadow property — the relationship survives — but you can no longer write
///       <c>new TicketComment { TicketId = 5 }</c> without first loading ticket 5.</item>
/// <item>The FK is non-nullable, so the relationship is required: a comment cannot exist
///       without a ticket, and deleting the ticket cascades to its comments.</item>
/// </list>
/// </remarks>
public class TicketComment : AuditableEntity
{
    public int Id { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// True = staff-only note, invisible to the citizen who reported the ticket.
    /// </summary>
    /// <remarks>
    /// Filtered out <em>server-side</em>, in the repository, for Citizen callers. Never rely
    /// on the front end to hide it: the JSON still went over the wire, and anyone can open
    /// the network tab. If a caller must not see a row, that row must not be in the response.
    /// </remarks>
    public bool IsInternal { get; set; }

    // ----- Required 1:N with the ticket -----

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    // ----- Optional 1:N with the author -----

    /// <summary>
    /// Nullable, and set from the token rather than the request body. Null means a system
    /// comment ("Status changed to Resolved by the nightly job").
    /// </summary>
    public int? AuthorId { get; set; }
    public ApplicationUser? Author { get; set; }

    /// <summary>
    /// Snapshot of the author's name at the time of writing.
    /// </summary>
    /// <remarks>
    /// Yes, this duplicates <c>Author.DisplayName</c>. On purpose: if the account is later
    /// renamed or deactivated, the comment should still read the way it read when it was
    /// posted. Denormalising a *historical* value is different from denormalising a *current*
    /// one — history is supposed to be frozen.
    /// </remarks>
    public string AuthorNameSnapshot { get; set; } = string.Empty;
}
