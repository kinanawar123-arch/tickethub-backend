using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;

namespace TicketHub.Contracts.Chat;

/// <summary>A chat thread. Either bolted to a ticket, or a direct/group conversation.</summary>
/// <remarks>
/// LIVE CHAT IS STILL JUST TABLES.
/// SignalR delivers the message to whoever is connected right now; the database is what makes
/// it survive a refresh. Both matter: broadcast without persistence loses history the moment
/// someone reloads, and persistence without broadcast means users press F5 to talk.
/// The service does both, in that order — <b>save first, then broadcast</b>, so a message
/// that was delivered is always a message that exists.
/// </remarks>
public class ConversationDto
{
    public int Id { get; init; }
    public ConversationType Type { get; init; }

    /// <summary>Set for ticket conversations; null for direct and group ones.</summary>
    public int? TicketId { get; init; }
    public string? TicketNumber { get; init; }

    /// <summary>What to show in the conversation list — the ticket title, or the other person's name.</summary>
    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<ConversationParticipantDto> Participants { get; init; } = Array.Empty<ConversationParticipantDto>();

    /// <summary>Preview text for the list screen. One projection, not a second query per row.</summary>
    public ChatMessageDto? LastMessage { get; init; }

    /// <summary>Unread messages for the caller, computed from their own LastReadAt.</summary>
    public int UnreadCount { get; init; }

    public DateTime CreatedAt { get; init; }
}

public class ConversationParticipantDto
{
    public int UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public DateTime? LastReadAt { get; init; }
}

/// <summary>One chat message. This is the shape SignalR pushes to clients.</summary>
public class ChatMessageDto
{
    public int Id { get; init; }
    public int ConversationId { get; init; }
    public string Body { get; init; } = string.Empty;

    public int SenderId { get; init; }
    public string SenderName { get; init; } = string.Empty;

    public DateTime SentAt { get; init; }
    public bool IsSystemMessage { get; init; }

    /// <summary>
    /// Echoed back untouched from <see cref="SendMessageDto.ClientMessageId"/>.
    /// The sending client draws the bubble immediately (optimistic UI) and uses this to match
    /// the server's broadcast to the bubble it already drew, instead of showing it twice.
    /// </summary>
    public string? ClientMessageId { get; init; }
}

public class SendMessageDto
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Body { get; set; } = string.Empty;

    /// <summary>A GUID the client makes up. See <see cref="ChatMessageDto.ClientMessageId"/>.</summary>
    [StringLength(64)]
    public string? ClientMessageId { get; set; }

    // No SenderId. Taken from the SignalR connection's authenticated user.
    // A hub method is as public as a controller action — the same rules apply.
}

public class StartConversationDto
{
    public ConversationType Type { get; set; } = ConversationType.Direct;

    /// <summary>Required for <see cref="ConversationType.Ticket"/> conversations.</summary>
    public int? TicketId { get; set; }

    /// <summary>The other user ids. The caller is added automatically.</summary>
    public List<int> ParticipantUserIds { get; set; } = new();

    [StringLength(120)]
    public string? Title { get; set; }
}

public class ConversationQuery : PagedQuery
{
    public ConversationType? Type { get; set; }
    public int? TicketId { get; set; }

    /// <summary>True = only conversations with something the caller has not read.</summary>
    public bool? UnreadOnly { get; set; }
}

public class MessageQuery : PagedQuery
{
    /// <summary>
    /// Cursor paging: "give me messages older than id N". Better than Skip/Take for a chat,
    /// because new messages arriving at the top do not shift everything and make you re-read
    /// a message you already saw.
    /// </summary>
    public int? BeforeMessageId { get; set; }
}

/// <summary>Payload of the <c>UserTyping</c> hub event. Never persisted.</summary>
public class TypingIndicatorDto
{
    public int ConversationId { get; init; }
    public int UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsTyping { get; init; }
}
