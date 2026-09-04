namespace TicketHub.Contracts.Enums;

/// <summary>The kind of thing a notification is telling you about.</summary>
public enum NotificationType
{
    TicketAssigned = 0,
    TicketStatusChanged = 1,
    CommentAdded = 2,
    TicketResolved = 3,
    ChatMessageReceived = 4,
    SlaBreachWarning = 5
}

/// <summary>Which side of the wall a chat conversation lives on.</summary>
public enum ConversationType
{
    /// <summary>Attached to one ticket: reporter ↔ assigned agent.</summary>
    Ticket = 0,

    /// <summary>Two people, no ticket. Agent ↔ agent, for example.</summary>
    Direct = 1,

    /// <summary>Three or more participants — a department war-room.</summary>
    Group = 2
}

/// <summary>
/// What happened to a row. Written to <c>AuditLogs</c> by the DbContext,
/// never by hand.
/// </summary>
public enum AuditAction
{
    Created = 0,
    Updated = 1,

    /// <summary>Soft delete — <c>IsDeleted</c> was set, the row is still there.</summary>
    Deleted = 2
}
