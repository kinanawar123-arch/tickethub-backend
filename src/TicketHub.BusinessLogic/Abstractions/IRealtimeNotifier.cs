using TicketHub.Contracts.Chat;
using TicketHub.Contracts.Notifications;

namespace TicketHub.BusinessLogic.Abstractions;

/// <summary>
/// "Push this to whoever is connected right now."
/// </summary>
/// <remarks>
/// THE DEPENDENCY INVERSION THAT MAKES THE CHAT FEATURE TESTABLE.
/// <para/>
/// SignalR lives in ASP.NET Core, which lives in the API project. The business layer sits
/// underneath the API and must not reference it — otherwise the dependency arrows point in a
/// circle and the whole four-project layout stops meaning anything.
/// <para/>
/// So the business layer declares what it <em>needs</em> (this interface) and the API supplies
/// how it is <em>done</em> (<c>SignalRNotifier</c>, which wraps <c>IHubContext</c>). The
/// arrow now points inwards: the outer layer depends on the inner one, never the reverse.
/// <para/>
/// The practical payoff: a unit test for "resolving a ticket notifies the reporter" passes a
/// fake notifier and asserts on it. No web server, no WebSocket, no waiting.
/// <para/>
/// Note that every method returns <c>Task</c> and none of them return a value. A push is
/// best-effort: if the recipient is offline it simply does not arrive, and that is not a
/// failure — the database row is the durable truth, and this is the convenience on top.
/// </remarks>
public interface IRealtimeNotifier
{
    /// <summary>Deliver a new chat message to everyone in the conversation.</summary>
    Task SendChatMessageAsync(int conversationId, ChatMessageDto message, CancellationToken ct = default);

    /// <summary>Someone started or stopped typing. Never persisted — pure presence.</summary>
    Task SendTypingIndicatorAsync(int conversationId, TypingIndicatorDto indicator, CancellationToken ct = default);

    /// <summary>Push a notification to one user, on whatever devices they have open.</summary>
    Task SendNotificationAsync(int userId, NotificationDto notification, CancellationToken ct = default);

    /// <summary>Tell everyone watching a ticket that it changed, so open detail pages refresh.</summary>
    Task SendTicketUpdatedAsync(int ticketId, object payload, CancellationToken ct = default);

    /// <summary>Add a user to a conversation's broadcast group so they start receiving it.</summary>
    Task AddUserToConversationAsync(int userId, int conversationId, CancellationToken ct = default);
}
