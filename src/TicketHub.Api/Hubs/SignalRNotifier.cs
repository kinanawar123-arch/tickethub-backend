using Microsoft.AspNetCore.SignalR;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.Contracts.Chat;
using TicketHub.Contracts.Notifications;

namespace TicketHub.Api.Hubs;

/// <summary>
/// The SignalR side of <see cref="IRealtimeNotifier"/>.
/// </summary>
/// <remarks>
/// This class is the reason the business layer can push messages without knowing SignalR
/// exists. It is the only implementation of the interface, it lives in the API project where
/// ASP.NET Core types are allowed, and it is registered in Program.cs.
/// <para/>
/// <b><see cref="IHubContext{THub}"/> vs being inside the hub.</b> Inside a hub method you
/// have <c>Clients</c> because you are handling a call from a client. Everywhere else — in a
/// service, a background job, a controller — there is no hub instance, and
/// <c>IHubContext</c> is how you reach the same clients from outside. It is a Singleton, and
/// safe to inject anywhere.
/// </remarks>
public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<SignalRNotifier> _log;

    public SignalRNotifier(IHubContext<ChatHub> hub, ILogger<SignalRNotifier> log)
    {
        _hub = hub;
        _log = log;
    }

    public Task SendChatMessageAsync(int conversationId, ChatMessageDto message, CancellationToken ct = default)
        // "ReceiveMessage" is the method name the CLIENT registers a handler for:
        //   connection.on("ReceiveMessage", msg => ...)
        // It is a magic string on both sides, so a typo means silence rather than an error.
        // Worth a shared constants file the day you have more than a handful of events.
        => _hub.Clients
               .Group(ChatHub.ConversationGroup(conversationId))
               .SendAsync("ReceiveMessage", message, ct);

    public Task SendTypingIndicatorAsync(int conversationId, TypingIndicatorDto indicator, CancellationToken ct = default)
        => _hub.Clients
               .Group(ChatHub.ConversationGroup(conversationId))
               .SendAsync("UserTyping", indicator, ct);

    public Task SendNotificationAsync(int userId, NotificationDto notification, CancellationToken ct = default)
        // Addressed to the USER group, not a connection id, so it reaches their laptop and
        // their phone at once. If they are offline it simply goes nowhere — and that is fine,
        // because the row is already in the database and the bell will show it next time.
        => _hub.Clients
               .Group(ChatHub.UserGroup(userId))
               .SendAsync("ReceiveNotification", notification, ct);

    public Task SendTicketUpdatedAsync(int ticketId, object payload, CancellationToken ct = default)
        => _hub.Clients
               .Group($"ticket-{ticketId}")
               .SendAsync("TicketUpdated", payload, ct);

    public Task AddUserToConversationAsync(int userId, int conversationId, CancellationToken ct = default)
    {
        // A real limitation, stated plainly: IHubContext can address groups and users, but it
        // cannot add someone else's CONNECTION to a group — only a hub method can, because
        // only it knows the connection id.
        //
        // So instead we tell the user's own connections to join. The client handles
        // "JoinedConversation" by calling the hub's JoinConversation method, which does have
        // a connection id to work with.
        _log.LogDebug("Asking user {UserId}'s connections to join conversation {ConversationId}",
            userId, conversationId);

        return _hub.Clients
                   .Group(ChatHub.UserGroup(userId))
                   .SendAsync("JoinedConversation", conversationId, ct);
    }
}
