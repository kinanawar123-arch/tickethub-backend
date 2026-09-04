using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Chat;

namespace TicketHub.Api.Hubs;

/// <summary>
/// The live-chat WebSocket endpoint. Clients connect to <c>/hubs/chat</c>.
/// </summary>
/// <remarks>
/// WHAT SIGNALR ACTUALLY GIVES YOU.
/// A plain HTTP API can only answer questions it was asked. To show a new message you would
/// have to poll — "anything new?" every two seconds, per user, forever — which is mostly
/// empty responses and still feels laggy. SignalR keeps a connection open so the server can
/// speak first.
/// <para/>
/// It negotiates the best available transport: WebSockets normally, Server-Sent Events or
/// long polling when something in the middle blocks them. You write the same code either way.
/// <para/>
/// THREE THINGS TO INTERNALISE ABOUT HUBS:
/// <list type="number">
/// <item><b>A hub method is a public endpoint.</b> Anyone who can open a socket can call
///       <c>SendMessage</c> with any conversation id they invent. It needs exactly the same
///       authorization a controller action does — which is why every method here delegates
///       to <see cref="IChatService"/>, where the membership check lives.</item>
/// <item><b>A hub instance is created per method call</b> and thrown away. Never store state
///       in a field expecting it to survive. Anything durable goes in the database.</item>
/// <item><b>Groups are the unit of broadcast.</b> A group is just a named set of connection
///       ids. We use one per conversation, so sending to everyone in a chat is one call and
///       no bookkeeping.</item>
/// </list>
/// </remarks>
[Authorize]   // no anonymous sockets: the connection must carry a valid token
public class ChatHub : Hub
{
    private readonly IChatService _chat;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ChatHub> _log;

    public ChatHub(IChatService chat, ICurrentUser currentUser, ILogger<ChatHub> log)
    {
        _chat = chat;
        _currentUser = currentUser;
        _log = log;
    }

    /// <summary>Group name for one conversation. One place, so it cannot drift.</summary>
    public static string ConversationGroup(int conversationId) => $"conversation-{conversationId}";

    /// <summary>Group name for one user, across all their devices.</summary>
    /// <remarks>
    /// A user with a laptop and a phone has two connections. Addressing them by user id
    /// rather than connection id means a notification reaches both, and we never have to
    /// track which connection belongs to whom.
    /// </remarks>
    public static string UserGroup(int userId) => $"user-{userId}";

    /// <summary>
    /// Runs when a client connects. Subscribes them to their own conversations.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var userId = _currentUser.UserId;

        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId.Value));

            // Join every conversation they are in, so messages arrive without the client
            // having to ask for each thread.
            //
            // Honest about the limit: a user in five hundred conversations makes this a slow
            // connect. At that point you subscribe lazily — join the group when they open a
            // thread. For a municipal ticket system, this is fine and much simpler.
            var conversations = await _chat.GetMyConversationsAsync(
                new ConversationQuery { PageSize = 100 }, Context.ConnectionAborted);

            foreach (var conversation in conversations.Items)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversation.Id));
            }

            _log.LogDebug("User {UserId} connected ({ConnectionId}), joined {Count} conversations",
                userId, Context.ConnectionId, conversations.Items.Count);
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // No cleanup needed: SignalR removes a dropped connection from its groups by itself.
        // Group membership is per-connection, not per-user, and dies with the connection.
        _log.LogDebug("Connection {ConnectionId} disconnected", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Send a message. Called by the client as
    /// <c>connection.invoke("SendMessage", conversationId, dto)</c>.
    /// </summary>
    /// <remarks>
    /// Note how little this method does. Validation, the membership check, persistence and
    /// the broadcast all live in <see cref="IChatService"/> — so the REST endpoint
    /// <c>POST /api/chat/conversations/{id}/messages</c> behaves identically. Two doors, one
    /// set of rules. Duplicating the logic in the hub is how the two doors quietly diverge.
    /// </remarks>
    public async Task<ChatMessageDto?> SendMessage(int conversationId, SendMessageDto dto)
    {
        var result = await _chat.SendMessageAsync(conversationId, dto, Context.ConnectionAborted);

        if (!result.IsSuccess)
        {
            // HubException is the one exception type whose message is sent to the client.
            // Anything else surfaces as a generic error — deliberately, so an unexpected
            // exception cannot leak a stack trace over the socket.
            throw new HubException(result.Error ?? "Could not send the message.");
        }

        // The service already broadcast it to the group. Returning it as well gives the
        // sender an immediate acknowledgement with the real id, so their optimistic bubble
        // can be reconciled without waiting for the group message to loop back.
        return result.Value;
    }

    /// <summary>Typing indicator. Never persisted.</summary>
    public async Task SetTyping(int conversationId, bool isTyping)
    {
        var result = await _chat.SetTypingAsync(conversationId, isTyping, Context.ConnectionAborted);

        if (!result.IsSuccess)
        {
            throw new HubException(result.Error ?? "Could not update the typing indicator.");
        }
    }

    /// <summary>Move the caller's read marker to now.</summary>
    public async Task MarkRead(int conversationId)
    {
        var result = await _chat.MarkReadAsync(conversationId, Context.ConnectionAborted);

        if (!result.IsSuccess)
        {
            throw new HubException(result.Error ?? "Could not mark the conversation as read.");
        }
    }

    /// <summary>
    /// Subscribe this connection to a conversation opened after connect time.
    /// </summary>
    public async Task JoinConversation(int conversationId)
    {
        // Membership is verified by the service before we add anyone to a broadcast group.
        // Skipping this would let any client subscribe to any conversation by number and
        // receive every message in it — a WebSocket leak that no HTTP log would show.
        var conversation = await _chat.GetAsync(conversationId, Context.ConnectionAborted);

        if (!conversation.IsSuccess)
        {
            throw new HubException("You are not a participant in that conversation.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
    }

    public Task LeaveConversation(int conversationId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
}
