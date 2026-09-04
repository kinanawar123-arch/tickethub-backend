using TicketHub.Contracts.Enums;

namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A bell-icon notification for one user.
/// </summary>
/// <remarks>
/// Stored first, pushed second. If we only pushed over SignalR, anyone offline at that moment
/// would simply never find out. The row is the truth; the real-time push is a convenience for
/// whoever happens to be connected.
/// </remarks>
public class Notification
{
    public int Id { get; set; }

    /// <summary>Who it is for. Required — a notification with no recipient is a log line.</summary>
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public NotificationType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>Client-side route to open on click, e.g. <c>/tickets/42</c>.</summary>
    public string? Link { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// The ticket this is about, when there is one. Optional and set to null on delete
    /// rather than cascading — a notification remains readable after its ticket is gone.
    /// </summary>
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
}
