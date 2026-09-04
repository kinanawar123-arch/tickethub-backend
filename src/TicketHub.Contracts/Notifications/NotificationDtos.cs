using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;

namespace TicketHub.Contracts.Notifications;

/// <summary>A bell-icon notification: "Ticket TKT-2026-000012 was assigned to you".</summary>
/// <remarks>
/// Notifications are stored, not only pushed. If we only pushed over SignalR, anyone who was
/// offline when it happened would never learn about it. So: write the row, then push it to
/// whoever is connected. The row is the truth; the push is a convenience.
/// </remarks>
public class NotificationDto
{
    public int Id { get; init; }
    public NotificationType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>Where clicking it should take you, e.g. <c>/tickets/42</c>.</summary>
    public string? Link { get; init; }

    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }
}

public class NotificationQuery : PagedQuery
{
    public bool? IsRead { get; set; }
    public NotificationType? Type { get; set; }
}

public class UnreadCountDto
{
    public int Count { get; init; }
}
