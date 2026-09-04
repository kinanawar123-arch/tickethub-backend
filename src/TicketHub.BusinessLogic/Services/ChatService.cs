using Microsoft.Extensions.Logging;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Chat;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

public interface IChatService
{
    Task<PagedResult<ConversationDto>> GetMyConversationsAsync(ConversationQuery query, CancellationToken ct = default);

    Task<ServiceResult<ConversationDto>> GetAsync(int conversationId, CancellationToken ct = default);

    Task<ServiceResult<ConversationDto>> StartAsync(StartConversationDto dto, CancellationToken ct = default);

    /// <summary>Opens (or reuses) the chat thread attached to a ticket.</summary>
    Task<ServiceResult<ConversationDto>> GetOrCreateForTicketAsync(int ticketId, CancellationToken ct = default);

    Task<ServiceResult<PagedResult<ChatMessageDto>>> GetMessagesAsync(
        int conversationId, MessageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Persists a message and broadcasts it. Called by the REST endpoint AND by the SignalR
    /// hub, so both doors behave identically.
    /// </summary>
    Task<ServiceResult<ChatMessageDto>> SendMessageAsync(
        int conversationId, SendMessageDto dto, CancellationToken ct = default);

    Task<ServiceResult> MarkReadAsync(int conversationId, CancellationToken ct = default);

    Task<ServiceResult> SetTypingAsync(int conversationId, bool isTyping, CancellationToken ct = default);
}

/// <summary>
/// Live chat. Two responsibilities, and the order between them is the whole design.
/// </summary>
/// <remarks>
/// <b>SAVE FIRST, BROADCAST SECOND.</b>
/// <list type="bullet">
/// <item>Broadcast without saving: the message appears, everyone sees it, and it is gone the
///       moment anyone refreshes. Users stop trusting the feature.</item>
/// <item>Save without broadcasting: nothing is lost, but people press F5 to hold a
///       conversation, which is not a chat.</item>
/// <item>Broadcast <em>before</em> saving: a delivered message that the save then rejects.
///       Now two people have seen something that does not exist.</item>
/// </list>
/// So: validate, persist, then push. Anything delivered is guaranteed to be in the database.
/// </remarks>
public class ChatService : IChatService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<ChatService> _log;

    public ChatService(
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IRealtimeNotifier notifier,
        ILogger<ChatService> log)
    {
        _uow = uow;
        _currentUser = currentUser;
        _notifier = notifier;
        _log = log;
    }

    public async Task<PagedResult<ConversationDto>> GetMyConversationsAsync(
        ConversationQuery query, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return PagedResult<ConversationDto>.Empty(query.Page, query.PageSize);
        }

        return await _uow.Chat.GetConversationsForUserAsync(_currentUser.UserId.Value, query, ct);
    }

    public async Task<ServiceResult<ConversationDto>> GetAsync(int conversationId, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<ConversationDto>.Forbidden();
        }

        // The membership check is inside the query, so a non-participant simply gets null —
        // and therefore a 404 rather than a 403 that would confirm the conversation exists.
        var conversation = await _uow.Chat.GetConversationAsync(conversationId, _currentUser.UserId.Value, ct);

        return conversation is null
            ? ServiceResult<ConversationDto>.NotFound("Conversation not found.")
            : ServiceResult<ConversationDto>.Success(conversation);
    }

    public async Task<ServiceResult<ConversationDto>> StartAsync(
        StartConversationDto dto, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<ConversationDto>.Forbidden();
        }

        var me = _currentUser.UserId.Value;

        // The caller is always a participant, whether or not they listed themselves.
        // Distinct(), because "me plus the people I picked" often includes me twice, and the
        // unique index on (ConversationId, UserId) would reject the insert.
        var participantIds = dto.ParticipantUserIds.Append(me).Distinct().ToList();

        if (participantIds.Count < 2)
        {
            return ServiceResult<ConversationDto>.Invalid("A conversation needs at least two people.");
        }

        var conversation = new Conversation
        {
            Type = dto.Type,
            Title = string.IsNullOrWhiteSpace(dto.Title) ? "Conversation" : dto.Title.Trim(),
            TicketId = dto.TicketId,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var userId in participantIds)
        {
            conversation.Participants.Add(new ConversationParticipant
            {
                UserId = userId,
                JoinedAt = DateTime.UtcNow,

                // The creator has "read" the empty conversation. Everyone else starts with
                // LastReadAt = null, which the unread count reads as "everything is new".
                LastReadAt = userId == me ? DateTime.UtcNow : null
            });
        }

        await _uow.Chat.AddAsync(conversation, ct);
        await _uow.SaveChangesAsync(ct);

        // Join everyone's live connections to the broadcast group, so the first message
        // reaches people who were already online when the conversation was created.
        foreach (var userId in participantIds)
        {
            await _notifier.AddUserToConversationAsync(userId, conversation.Id, ct);
        }

        return await GetAsync(conversation.Id, ct);
    }

    public async Task<ServiceResult<ConversationDto>> GetOrCreateForTicketAsync(
        int ticketId, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<ConversationDto>.Forbidden();
        }

        var existing = await _uow.Chat.GetForTicketAsync(ticketId, ct);
        if (existing is not null)
        {
            // The thread exists but the caller may have joined the ticket since — a newly
            // assigned agent, for instance. Add them rather than refusing.
            if (existing.Participants.All(p => p.UserId != _currentUser.UserId.Value))
            {
                existing.Participants.Add(new ConversationParticipant
                {
                    UserId = _currentUser.UserId.Value,
                    JoinedAt = DateTime.UtcNow
                });

                await _uow.SaveChangesAsync(ct);
                await _notifier.AddUserToConversationAsync(_currentUser.UserId.Value, existing.Id, ct);
            }

            return await GetAsync(existing.Id, ct);
        }

        var ticket = await _uow.Tickets.GetByIdAsync(ticketId, ct);
        if (ticket is null)
        {
            return ServiceResult<ConversationDto>.NotFound($"Ticket {ticketId} was not found.");
        }

        // The thread's members are whoever the ticket concerns: the reporter, the assigned
        // agent, and the caller.
        var participants = new List<int>();

        if (ticket.CreatedByUserId.HasValue)
        {
            participants.Add(ticket.CreatedByUserId.Value);
        }

        if (ticket.AssignedAgentId.HasValue)
        {
            var agent = await _uow.Agents.GetByIdAsync(ticket.AssignedAgentId.Value, ct);
            if (agent is not null)
            {
                participants.Add(agent.UserId);
            }
        }

        participants.Add(_currentUser.UserId.Value);

        return await StartAsync(new StartConversationDto
        {
            Type = ConversationType.Ticket,
            TicketId = ticketId,
            Title = $"{ticket.TicketNumber} — {ticket.Title}",
            ParticipantUserIds = participants.Distinct().ToList()
        }, ct);
    }

    public async Task<ServiceResult<PagedResult<ChatMessageDto>>> GetMessagesAsync(
        int conversationId, MessageQuery query, CancellationToken ct = default)
    {
        var guard = await EnsureParticipantAsync(conversationId, ct);
        if (guard is not null)
        {
            return ServiceResult<PagedResult<ChatMessageDto>>.Forbidden(guard);
        }

        var messages = await _uow.Chat.GetMessagesAsync(conversationId, query, ct);
        return ServiceResult<PagedResult<ChatMessageDto>>.Success(messages);
    }

    // =====================================================================
    // The one that matters
    // =====================================================================

    public async Task<ServiceResult<ChatMessageDto>> SendMessageAsync(
        int conversationId, SendMessageDto dto, CancellationToken ct = default)
    {
        // 1. AUTHORIZE. First, always, and on both doors.
        //
        // This method is reachable from a controller AND from a SignalR hub. A hub method is
        // as publicly callable as any endpoint: anyone who can open a WebSocket can invoke
        // SendMessage(conversationId: 999, ...) with any number they like. Putting the check
        // here — rather than in the hub — means both doors are locked by the same code.
        var guard = await EnsureParticipantAsync(conversationId, ct);
        if (guard is not null)
        {
            return ServiceResult<ChatMessageDto>.Forbidden(guard);
        }

        // 2. IDEMPOTENCE. A dropped connection makes clients retry; the retry carries the
        // same ClientMessageId. Recognising it means a flaky network does not double every
        // message. (The unique index is the real guarantee — this just avoids the exception.)
        if (!string.IsNullOrWhiteSpace(dto.ClientMessageId))
        {
            var duplicate = await _uow.Chat.FindByClientMessageIdAsync(
                conversationId, dto.ClientMessageId, ct);

            if (duplicate is not null)
            {
                var existing = await _uow.Chat.GetMessageDtoAsync(duplicate.Id, ct);
                return existing is null
                    ? ServiceResult<ChatMessageDto>.NotFound()
                    : ServiceResult<ChatMessageDto>.Success(existing);
            }
        }

        var conversation = await _uow.Chat.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            return ServiceResult<ChatMessageDto>.NotFound("Conversation not found.");
        }

        var now = DateTime.UtcNow;

        var message = new ChatMessage
        {
            ConversationId = conversationId,
            SenderId = _currentUser.UserId,
            SenderNameSnapshot = _currentUser.DisplayName ?? _currentUser.Email ?? "Unknown",
            Body = dto.Body.Trim(),
            SentAt = now,
            ClientMessageId = dto.ClientMessageId
        };

        await _uow.Repository<ChatMessage>().AddAsync(message, ct);

        // Keep the denormalised column in step, in the same save. Without this the inbox
        // would need MAX(SentAt) per conversation on every load.
        conversation.LastMessageAt = now;

        // 3. PERSIST. One save: message and conversation together.
        await _uow.SaveChangesAsync(ct);

        var dtoOut = new ChatMessageDto
        {
            Id = message.Id,
            ConversationId = conversationId,
            Body = message.Body,
            SenderId = message.SenderId ?? 0,
            SenderName = message.SenderNameSnapshot,
            SentAt = message.SentAt,
            IsSystemMessage = false,
            ClientMessageId = message.ClientMessageId
        };

        // 4. BROADCAST — only now that the row definitely exists.
        await _notifier.SendChatMessageAsync(conversationId, dtoOut, ct);

        _log.LogDebug("Message {MessageId} sent to conversation {ConversationId}",
            message.Id, conversationId);

        return ServiceResult<ChatMessageDto>.Success(dtoOut);
    }

    public async Task<ServiceResult> MarkReadAsync(int conversationId, CancellationToken ct = default)
    {
        var guard = await EnsureParticipantAsync(conversationId, ct);
        if (guard is not null)
        {
            return ServiceResult.Forbidden(guard);
        }

        await _uow.Chat.MarkReadAsync(conversationId, _currentUser.UserId!.Value, ct);
        return ServiceResult.Success();
    }

    public async Task<ServiceResult> SetTypingAsync(
        int conversationId, bool isTyping, CancellationToken ct = default)
    {
        var guard = await EnsureParticipantAsync(conversationId, ct);
        if (guard is not null)
        {
            return ServiceResult.Forbidden(guard);
        }

        // Deliberately NOT persisted. "Sara is typing…" is worthless three seconds later and
        // writing it would mean a database round trip per keystroke. Some state belongs only
        // in the moment.
        await _notifier.SendTypingIndicatorAsync(conversationId, new TypingIndicatorDto
        {
            ConversationId = conversationId,
            UserId = _currentUser.UserId ?? 0,
            DisplayName = _currentUser.DisplayName ?? "Someone",
            IsTyping = isTyping
        }, ct);

        return ServiceResult.Success();
    }

    // ---------------------------------------------------------------------

    /// <summary>Returns null when the caller is a participant, or the refusal message if not.</summary>
    private async Task<string?> EnsureParticipantAsync(int conversationId, CancellationToken ct)
    {
        if (_currentUser.UserId is null)
        {
            return "You must be signed in to use chat.";
        }

        var isParticipant = await _uow.Chat.IsParticipantAsync(conversationId, _currentUser.UserId.Value, ct);

        return isParticipant ? null : "You are not a participant in this conversation.";
    }
}
