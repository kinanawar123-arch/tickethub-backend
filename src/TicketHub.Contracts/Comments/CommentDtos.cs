using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Common;

namespace TicketHub.Contracts.Comments;

/// <summary>
/// A comment on a ticket — the conversation trail that explains how the ticket got resolved.
/// </summary>
/// <remarks>
/// WHY A COMMENT TABLE AND NOT A BIG "Notes" TEXT COLUMN ON Ticket?
/// A text column cannot answer any of the questions you will be asked within a month:
/// who wrote this, when, was it visible to the citizen, and can we delete just that one
/// paragraph. Rows can. It is the classic 1:N: the FK (<c>TicketId</c>) lives on the many
/// side, and the ticket holds a collection.
/// <para/>
/// Do not confuse this with <c>ChatMessage</c>. A comment is a durable record on the ticket
/// that survives the conversation; a chat message is live, ephemeral back-and-forth. They
/// look similar in the database and mean completely different things to the business.
/// </remarks>
public class CommentDto
{
    public int Id { get; init; }
    public int TicketId { get; init; }
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// True = staff-only. Never returned to a Citizen caller — the repository filters
    /// these out server-side rather than trusting the front end to hide them.
    /// </summary>
    public bool IsInternal { get; init; }

    public int? AuthorId { get; init; }
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>True when the author is the assigned agent, so the UI can style it differently.</summary>
    public bool IsFromAgent { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }

    /// <summary>True if this comment was edited after it was posted. Honesty in the timeline.</summary>
    public bool WasEdited => UpdatedAt.HasValue;
}

public class CreateCommentDto
{
    [Required]
    [StringLength(2000, MinimumLength = 1,
        ErrorMessage = "A comment needs between 1 and 2000 characters.")]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Staff-only note. The service ignores this (forces false) when the caller is a Citizen —
    /// otherwise a citizen could post a comment invisible to the very staff meant to read it.
    /// </summary>
    public bool IsInternal { get; set; }

    // No TicketId: it comes from the route (/api/tickets/{ticketId}/comments).
    // No AuthorId: it comes from the token. Same rule as CreateTicketDto.
}

public class UpdateCommentDto
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Body { get; set; } = string.Empty;
}

public class CommentQuery : PagedQuery
{
    /// <summary>False = hide internal notes. Forced to false for Citizen callers.</summary>
    public bool IncludeInternal { get; set; } = true;
}
