namespace TicketHub.DataAccess.Entities;

/// <summary>
/// One recorded change to a ticket, for the timeline on the detail page.
/// </summary>
/// <remarks>
/// HOW IS THIS DIFFERENT FROM <see cref="AuditLog"/>?
/// <list type="bullet">
/// <item><b>This</b> is a <em>domain</em> record. It is written deliberately by
///       <c>TicketService</c> when something business-meaningful happens, and it is shown to
///       users: "Sara changed the status from Open to In Progress".</item>
/// <item><b>AuditLog</b> is a <em>technical</em> record. The DbContext writes it for every
///       insert/update/delete on every audited table, nobody sees it in the UI, and it exists
///       for the question "who changed this row and when" months later.</item>
/// </list>
/// Keeping them apart means the user-facing timeline stays readable instead of filling up
/// with "UpdatedAt changed from null to 2026-08-03T09:14:22Z".
/// </remarks>
public class TicketHistory
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    /// <summary>Which field changed: "Status", "Priority", "AssignedAgent", "Category".</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Stored as text because this table holds changes to fields of many types.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>Free text from the person making the change: "closing, duplicate of #88".</summary>
    public string? Note { get; set; }

    public DateTime ChangedAt { get; set; }

    public int? ChangedById { get; set; }
    public ApplicationUser? ChangedBy { get; set; }

    /// <summary>Frozen at write time — see the same note on <see cref="TicketComment.AuthorNameSnapshot"/>.</summary>
    public string? ChangedByName { get; set; }

    // No AuditableEntity here: a history row is already an audit record. Auditing the audit
    // trail is how you end up with a database made entirely of metadata.
}
