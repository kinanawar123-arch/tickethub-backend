using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Chat;
using TicketHub.Contracts.Common;

namespace TicketHub.Api.Controllers;

/// <summary>
/// The REST half of live chat. The WebSocket half is <c>ChatHub</c> at <c>/hubs/chat</c>.
/// </summary>
/// <remarks>
/// WHY BOTH? They do different jobs, and a chat needs both:
/// <list type="bullet">
/// <item><b>REST</b> loads history. When you open a conversation you need the last fifty
///       messages, and asking for them over a socket is awkward and unnecessary.</item>
/// <item><b>SignalR</b> delivers what arrives while you are looking at it.</item>
/// </list>
/// Both go through <see cref="IChatService"/>, so sending a message over HTTP and sending it
/// over the socket do exactly the same thing — including the authorization check. Two doors,
/// one lock. Duplicating the logic in the hub is how the two doors quietly stop agreeing.
/// <para/>
/// The REST send endpoint also matters for clients that cannot hold a socket open: a mobile
/// app in the background, a cURL script, an integration.
/// </remarks>
[Route("api/chat")]
[Authorize]
[Produces("application/json")]
public class ChatController : ApiControllerBase
{
    private readonly IChatService _chat;

    public ChatController(IChatService chat) => _chat = chat;

    /// <summary>The caller's conversations, most recent first, with unread counts.</summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(PagedResult<ConversationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ConversationDto>>> GetConversations(
        [FromQuery] ConversationQuery query, CancellationToken ct)
        => Ok(await _chat.GetMyConversationsAsync(query, ct));

    /// <summary>One conversation. 404 if the caller is not a participant.</summary>
    [HttpGet("conversations/{id:int}", Name = nameof(GetConversation))]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDto>> GetConversation(int id, CancellationToken ct)
        => ToActionResult(await _chat.GetAsync(id, ct));

    /// <summary>Starts a direct or group conversation.</summary>
    [HttpPost("conversations")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ConversationDto>> StartConversation(
        [FromBody] StartConversationDto dto, CancellationToken ct)
    {
        var result = await _chat.StartAsync(dto, ct);
        return CreatedResult(result, nameof(GetConversation), new { id = result.Value?.Id });
    }

    /// <summary>Opens the chat thread for a ticket, creating it the first time.</summary>
    /// <remarks>
    /// Get-or-create, so the client does not have to check first and then create — two calls
    /// with a race in between. One idempotent call is both simpler and correct.
    /// </remarks>
    [HttpPost("tickets/{ticketId:int}/conversation")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ConversationDto>> GetOrCreateTicketConversation(
        int ticketId, CancellationToken ct)
        => ToActionResult(await _chat.GetOrCreateForTicketAsync(ticketId, ct));

    /// <summary>Message history, newest first.</summary>
    /// <remarks>
    /// Pass <c>beforeMessageId</c> to scroll back. Cursor paging, not Skip/Take — see the
    /// comment in <c>ChatRepository.GetMessagesAsync</c> for why that matters in a chat.
    /// </remarks>
    [HttpGet("conversations/{id:int}/messages")]
    [ProducesResponseType(typeof(PagedResult<ChatMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<ChatMessageDto>>> GetMessages(
        int id, [FromQuery] MessageQuery query, CancellationToken ct)
        => ToActionResult(await _chat.GetMessagesAsync(id, query, ct));

    /// <summary>Sends a message over HTTP. Identical in effect to the hub's SendMessage.</summary>
    /// <remarks>
    /// The service broadcasts over SignalR after saving, so anyone with the conversation open
    /// sees it immediately even though it arrived over plain HTTP.
    /// </remarks>
    [HttpPost("conversations/{id:int}/messages")]
    [ProducesResponseType(typeof(ChatMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ChatMessageDto>> SendMessage(
        int id, [FromBody] SendMessageDto dto, CancellationToken ct)
        => ToActionResult(await _chat.SendMessageAsync(id, dto, ct));

    /// <summary>Moves the caller's read marker to now.</summary>
    [HttpPost("conversations/{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MarkRead(int id, CancellationToken ct)
        => ToActionResult(await _chat.MarkReadAsync(id, ct));
}
