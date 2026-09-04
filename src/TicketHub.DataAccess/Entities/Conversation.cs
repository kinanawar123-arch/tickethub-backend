using TicketHub.Contracts.Enums;

namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A chat thread. The live-chat feature is three tables and a SignalR hub — no more.
/// </summary>
/// <remarks>
/// THE SHAPE OF A CHAT, AND WHY IT IS THIS SHAPE.
/// <list type="number">
/// <item><b>Conversation</b> — the thread. Exists so a message has somewhere to belong and so
///       "who is in this chat" has one answer.</item>
/// <item><b>ConversationParticipant</b> — a join row between conversation and user. This is
///       an N:N that carries data (<c>LastReadAt</c>), which is exactly the case where you
///       must write the join entity by hand instead of letting EF invent it.</item>
/// <item><b>ChatMessage</b> — the message itself. 1:N off the conversation.</item>
/// </list>
/// The participant row is also the authorization check: before the hub broadcasts anything,
/// it verifies the sender is a participant. A hub method is as publicly reachable as a
/// controller action — anyone who can open a WebSocket can call it with any conversation id
/// they like.
/// </remarks>
public class Conversation : AuditableEntity
{
    public int Id { get; set; }

    public ConversationType Type { get; set; } = ConversationType.Direct;

    /// <summary>Shown in the conversation list. For ticket chats we fill it with the ticket title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Set for <see cref="ConversationType.Ticket"/> conversations, null otherwise.
    /// Unique when present — one chat thread per ticket, enforced by a filtered unique index.
    /// </summary>
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// Denormalised copy of the newest message's timestamp.
    /// </summary>
    /// <remarks>
    /// Without it, sorting the conversation list by recent activity means a correlated
    /// subquery per row — the classic slow inbox. Updating one column when a message is sent
    /// is far cheaper than paying for that ordering on every list load.
    /// </remarks>
    public DateTime? LastMessageAt { get; set; }

    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

/// <summary>
/// One person's membership of one conversation — and the N:N join we had to write by hand.
/// </summary>
/// <remarks>
/// <c>Agent</c> ↔ <c>Skill</c> is an N:N with nothing to say about the pairing, so EF creates
/// the join table itself and there is no class to write. This one carries
/// <see cref="LastReadAt"/>, and the moment a join carries data of its own it stops being
/// plumbing and becomes an entity. That is the whole rule.
/// </remarks>
public class ConversationParticipant
{
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// Everything after this is unread. One nullable timestamp replaces an
    /// is-read flag per message per user, which would be a table that grows as
    /// messages × participants.
    /// </summary>
    public DateTime? LastReadAt { get; set; }

    /// <summary>The user muted this thread. Suppresses notifications, not delivery.</summary>
    public bool IsMuted { get; set; }
}

/// <summary>One chat message.</summary>
public class ChatMessage
{
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    /// <summary>Null for system messages ("Ahmad joined the conversation").</summary>
    public int? SenderId { get; set; }
    public ApplicationUser? Sender { get; set; }

    /// <summary>Frozen at send time, like every other name snapshot in this project.</summary>
    public string SenderNameSnapshot { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }

    public bool IsSystemMessage { get; set; }

    /// <summary>
    /// The id the sending client invented before it knew our id.
    /// </summary>
    /// <remarks>
    /// A good chat UI draws the bubble the instant you press Enter, before the server has
    /// heard about it. When the server's broadcast comes back, the client needs to know that
    /// this is the same message rather than a second one — so it matches on this. Without it
    /// you see every message you send twice for a fraction of a second.
    /// <para/>
    /// It also makes the send idempotent: a retry after a dropped connection carries the same
    /// value, and we can recognise it instead of storing the message twice.
    /// </remarks>
    public string? ClientMessageId { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
