using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Attachments;
using TicketHub.Contracts.Comments;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Ratings;
using TicketHub.Contracts.Tickets;
using TicketHub.Contracts.Workflow;

namespace TicketHub.Api.Controllers;

/// <summary>
/// Tickets — the main resource of the API.
/// </summary>
/// <remarks>
/// READ THIS CONTROLLER AS A TEMPLATE. Every action does the same four things: bind, call the
/// service, translate the result, and nothing else. There is not a single business rule in
/// this file, and there should never be one.
/// <para/>
/// <b>REST shapes used here, and why:</b>
/// <list type="bullet">
/// <item><c>GET /api/tickets</c> — a collection. Filters go in the query string, never in a
///       POST body, so the URL is shareable and cacheable.</item>
/// <item><c>GET /api/tickets/{id}</c> — one resource.</item>
/// <item><c>POST /api/tickets</c> — create. Returns 201 with a Location header.</item>
/// <item><c>PUT /api/tickets/{id}</c> — replace the editable fields. Idempotent: sending it
///       twice leaves the same state.</item>
/// <item><c>PATCH /api/tickets/{id}/status</c> — a named sub-operation. Not a PUT, because
///       it is not "replace the ticket", it is "move it through the workflow", and it has
///       its own rules and its own side effects.</item>
/// <item><c>DELETE /api/tickets/{id}</c> — remove. 204, no body.</item>
/// </list>
/// </remarks>
[Route("api/tickets")]
[Authorize]   // everything here needs a signed-in caller; individual actions narrow it further
[Produces("application/json")]
public class TicketsController : ApiControllerBase
{
    private readonly ITicketService _tickets;
    private readonly ICommentService _comments;
    private readonly IAttachmentService _attachments;
    private readonly IRatingService _ratings;

    // Constructor injection. The container supplies these; the controller never news anything
    // up, which is what makes every dependency swappable in a test.
    public TicketsController(
        ITicketService tickets,
        ICommentService comments,
        IAttachmentService attachments,
        IRatingService ratings)
    {
        _tickets = tickets;
        _comments = comments;
        _attachments = attachments;
        _ratings = ratings;
    }

    // =====================================================================
    // Tickets
    // =====================================================================

    /// <summary>Lists tickets the caller is allowed to see, filtered and paged.</summary>
    /// <remarks>
    /// There is no <c>departmentId</c> the caller can set to see another department's work —
    /// well, there is a filter, but the service intersects it with what their token allows.
    /// Filtering is a convenience; the access rule is not negotiable.
    /// </remarks>
    /// <response code="200">A page of tickets. Empty list if nothing matched.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<TicketListItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TicketListItemDto>>> Search(
        // [FromQuery] on a complex type binds every matching query-string parameter onto its
        // properties: ?status=1&priority=3&page=2 all land on one object.
        [FromQuery] TicketQuery query,

        // The CancellationToken is bound automatically and fires when the client disconnects.
        // Passed all the way down to EF, it means a user who closes the tab stops a slow
        // query instead of leaving it running for nobody.
        CancellationToken ct)
        => Ok(await _tickets.SearchAsync(query, ct));

    // ---------------------------------------------------------------------
    // Literal routes first — "mine" and "statistics" before "{id:int}"
    // ---------------------------------------------------------------------
    //
    // BE PRECISE ABOUT WHY, because the usual explanation is folklore. ASP.NET Core's
    // attribute routing does NOT match in declaration order: it ranks candidates by
    // precedence — a literal segment beats a constrained parameter, which beats a bare one.
    // So "mine" would win against "{id:int}" wherever it appeared in the file, and the :int
    // constraint means "{id:int}" cannot match the word "mine" in the first place.
    //
    // Two reasons to group them up here anyway:
    //   • The habit travels. Plenty of routers (and our own conventional routes, if we ever
    //     add one) DO match in order, and "specific before general" is right in all of them.
    //   • Drop the constraint to "{id}" and precedence is all that is protecting you. It
    //     still holds — but you now have to know that, rather than being able to see it.
    //
    // What actually goes wrong here is a TIE: two routes of equal precedence match the same
    // URL and ASP.NET Core throws AmbiguousMatchException — at request time, not at start-up,
    // so the endpoint that has worked for a year breaks the day somebody adds its twin.

    /// <summary>The caller's own tickets: assigned to them if staff, reported by them if a citizen.</summary>
    /// <remarks>
    /// No user id in the route. "Mine" is answered from the token, so there is no parameter
    /// for anyone to change to somebody else's number — the whole class of "I edited the id in
    /// the URL and saw another user's data" simply has nowhere to happen.
    /// </remarks>
    /// <response code="200">A page of tickets. Empty if they have none.</response>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(PagedResult<TicketListItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TicketListItemDto>>> GetMine(
        [FromQuery] TicketQuery query, CancellationToken ct)
        => Ok(await _tickets.GetMineAsync(query, ct));

    /// <summary>Dashboard counts, scoped to what the caller can see.</summary>
    [HttpGet("statistics")]
    [Authorize(Roles = AppRoles.Staff)]
    [ProducesResponseType(typeof(TicketStatisticsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketStatisticsDto>> Statistics(CancellationToken ct)
        => ToActionResult(await _tickets.GetStatisticsAsync(ct));

    /// <summary>One ticket with its comments, attachments, history and rating.</summary>
    /// <response code="200">The ticket.</response>
    /// <response code="404">No such ticket, or the caller may not see it.</response>
    [HttpGet("{id:int}", Name = nameof(GetTicket))]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketDetailDto>> GetTicket(int id, CancellationToken ct)
        => ToActionResult(await _tickets.GetByIdAsync(id, ct));

    /// <summary>Where the ticket is in the workflow, and which moves this caller may make.</summary>
    /// <remarks>
    /// THE ENDPOINT THAT LETS A SCREEN BE HONEST. <c>GET /api/workflow/transitions</c> gives
    /// the map for every status; this gives the answer for one ticket and one caller — the
    /// legal destinations from where it is now, each flagged with whether a reason is required
    /// and whether <em>you</em> are allowed to be the one to do it.
    /// <para/>
    /// With it, a client renders exactly the buttons that will work. Without it, it either
    /// guesses or shows everything and lets the user discover the rules through error
    /// messages.
    /// </remarks>
    /// <response code="200">The current state and its allowed transitions.</response>
    /// <response code="404">No such ticket, or the caller may not see it.</response>
    [HttpGet("{id:int}/workflow")]
    [ProducesResponseType(typeof(TicketWorkflowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketWorkflowDto>> Workflow(int id, CancellationToken ct)
        => ToActionResult(await _tickets.GetWorkflowAsync(id, ct));

    /// <summary>The ticket's audited timeline, newest first.</summary>
    /// <remarks>
    /// The detail response already carries the history, so this is for the ticket that has
    /// been through forty changes and needs paging rather than all of it on every open.
    /// </remarks>
    /// <response code="200">A page of history rows.</response>
    /// <response code="404">No such ticket, or the caller may not see it.</response>
    [HttpGet("{id:int}/history")]
    [ProducesResponseType(typeof(PagedResult<TicketHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<TicketHistoryDto>>> History(
        int id, [FromQuery] TicketHistoryQuery query, CancellationToken ct)
        => ToActionResult(await _tickets.GetHistoryAsync(id, query, ct));

    /// <summary>Reports a new ticket. No account required.</summary>
    /// <remarks>
    /// ⚠ THE ONE ANONYMOUS WRITE IN THE API, and a deliberate one: a citizen who must register
    /// before they can report a burst water main mostly does not report it. Reporting is the
    /// front door of the whole system, so it is open.
    /// <para/>
    /// What that changes, and what it does not:
    /// <list type="bullet">
    /// <item><b>Signed in</b> — exactly as before. <c>CreatedByUserId</c> comes from the token,
    ///       the ticket appears in their "mine" list, and status changes notify them.</item>
    /// <item><b>Anonymous</b> — <c>CreatedByUserId</c> stays null, and the reporter's email or
    ///       phone becomes required instead, because otherwise there is no way to tell them
    ///       what happened. The service enforces that; see <c>TicketService.CreateAsync</c>.</item>
    /// </list>
    /// The identity still comes from the token and never from the body. <c>[AllowAnonymous]</c>
    /// means "there may be no token", not "believe what the body says about who I am" — that
    /// is why <c>CreateTicketDto</c> still has no <c>CreatedByUserId</c> on it.
    /// <para/>
    /// Uploading a file is a different matter and still requires a token: an unauthenticated
    /// endpoint that writes bytes to our disk is a free hosting service for whatever the
    /// internet feels like storing.
    /// </remarks>
    /// <response code="201">Created. The Location header points at the new ticket.</response>
    /// <response code="400">Validation failed, the category does not exist, or an anonymous
    /// report arrived with no way to contact the reporter.</response>
    [HttpPost]
    [AllowAnonymous]
    // Rate limited, because it is now reachable without a token. The "global" policy is a
    // per-IP fixed window — it will not stop a determined spammer, but it does stop one
    // script from filling the ticket table overnight. See Program.cs for the policies.
    [EnableRateLimiting("global")]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TicketDetailDto>> Create(
        [FromBody] CreateTicketDto dto, CancellationToken ct)
    {
        // NOTE what is NOT here: no "if (!ModelState.IsValid) return BadRequest(ModelState)".
        // [ApiController] on the base class does that automatically, before this method is
        // ever entered. Writing the check by hand is a leftover habit from older ASP.NET.
        var result = await _tickets.CreateAsync(dto, ct);

        return CreatedResult(result, nameof(GetTicket), new { id = result.Value?.Id });
    }

    /// <summary>Edits the ticket's own fields. Not its status, and not its assignment.</summary>
    /// <response code="409">Someone else changed the ticket first (concurrency), or it is closed.</response>
    [HttpPut("{id:int}")]
    [Authorize(Roles = AppRoles.Staff)]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TicketDetailDto>> Update(
        int id, [FromBody] UpdateTicketDto dto, CancellationToken ct)
        => ToActionResult(await _tickets.UpdateAsync(id, dto, ct));

    /// <summary>Moves the ticket through the workflow.</summary>
    /// <remarks>
    /// PATCH on a named sub-resource, not PUT on the ticket. A status change writes a history
    /// row, fires a notification, and is refused if the workflow says so — that is an
    /// operation, not a field assignment.
    /// </remarks>
    /// <response code="409">The workflow does not allow that transition.</response>
    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = AppRoles.Staff)]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TicketDetailDto>> ChangeStatus(
        int id, [FromBody] ChangeTicketStatusDto dto, CancellationToken ct)
        => ToActionResult(await _tickets.ChangeStatusAsync(id, dto, ct));

    /// <summary>Sends a Resolved ticket back to In Progress. "It is not actually fixed."</summary>
    /// <remarks>
    /// NOTICE WHAT IS MISSING: no <c>[Authorize(Roles = …)]</c>. This is the one transition a
    /// citizen may make, and only on the ticket they themselves reported — which is a fact
    /// about a <em>row</em>, not about a role, so no attribute can express it. The check lives
    /// in <c>TicketService.ReopenAsync</c>, where the ticket is in front of it.
    /// <para/>
    /// That is also why this is not just a value posted to the status endpoint: relaxing that
    /// endpoint's staff-only rule to let one transition through would let all six through.
    /// </remarks>
    /// <response code="200">Re-opened.</response>
    /// <response code="403">Signed in, but neither staff nor the reporter of this ticket.</response>
    /// <response code="409">The ticket is not Resolved, so there is nothing to re-open.</response>
    [HttpPost("{id:int}/reopen")]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TicketDetailDto>> Reopen(int id, CancellationToken ct)
        => ToActionResult(await _tickets.ReopenAsync(id, ct));

    /// <summary>Assigns the ticket to an agent, or clears the assignment.</summary>
    /// <remarks>
    /// Deciding who does the work is a supervisor's job, so an Agent cannot hand themselves
    /// the interesting tickets. This is the kind of rule roles express well.
    /// </remarks>
    [HttpPatch("{id:int}/assign")]
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailDto>> Assign(
        int id, [FromBody] AssignTicketDto dto, CancellationToken ct)
        => ToActionResult(await _tickets.AssignAsync(id, dto, ct));

    /// <summary>Assigns to the least-loaded agent in the ticket's department.</summary>
    [HttpPost("{id:int}/auto-assign")]
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
    [ProducesResponseType(typeof(TicketDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailDto>> AutoAssign(int id, CancellationToken ct)
        => ToActionResult(await _tickets.AutoAssignAsync(id, ct));

    /// <summary>Soft-deletes a ticket. The row survives; it just stops being visible.</summary>
    /// <response code="204">Deleted. No body — there is nothing left to return.</response>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = AppRoles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
        => ToActionResult(await _tickets.DeleteAsync(id, ct));

    // =====================================================================
    // Comments — nested under the ticket they belong to
    // =====================================================================
    //
    // /api/tickets/42/comments rather than /api/comments?ticketId=42.
    // The nested URL says what a comment IS: something that cannot exist without a ticket.
    // It also makes the authorization obvious — you check the ticket, then the comment.

    /// <summary>The ticket's comments. Internal notes are omitted for Citizen callers.</summary>
    [HttpGet("{ticketId:int}/comments")]
    [ProducesResponseType(typeof(PagedResult<CommentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CommentDto>>> GetComments(
        int ticketId, [FromQuery] CommentQuery query, CancellationToken ct)
        => ToActionResult(await _comments.GetForTicketAsync(ticketId, query, ct));

    /// <summary>Adds a comment.</summary>
    [HttpPost("{ticketId:int}/comments")]
    [ProducesResponseType(typeof(CommentDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CommentDto>> AddComment(
        int ticketId, [FromBody] CreateCommentDto dto, CancellationToken ct)
        => ToActionResult(await _comments.AddAsync(ticketId, dto, ct));

    /// <summary>Edits a comment. Only its author, or an Admin.</summary>
    [HttpPut("{ticketId:int}/comments/{commentId:int}")]
    [ProducesResponseType(typeof(CommentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CommentDto>> UpdateComment(
        int ticketId, int commentId, [FromBody] UpdateCommentDto dto, CancellationToken ct)
        => ToActionResult(await _comments.UpdateAsync(commentId, dto, ct));

    /// <summary>Soft-deletes a comment. Only its author, or an Admin.</summary>
    [HttpDelete("{ticketId:int}/comments/{commentId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteComment(int ticketId, int commentId, CancellationToken ct)
        => ToActionResult(await _comments.DeleteAsync(commentId, ct));

    // =====================================================================
    // Attachments
    // =====================================================================

    /// <summary>The ticket's attachments — names, sizes and download links, not the files.</summary>
    /// <remarks>
    /// One small JSON response describes twelve photos; the browser then fetches only the one
    /// the user clicks, from the <c>downloadUrl</c> on each row. Returning the files here
    /// instead would turn "show me the attachments" into a forty-megabyte request.
    /// </remarks>
    /// <response code="200">The list. Empty if nothing has been attached.</response>
    /// <response code="404">No such ticket, or the caller may not see it.</response>
    [HttpGet("{ticketId:int}/attachments")]
    [ProducesResponseType(typeof(IReadOnlyList<AttachmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> GetAttachments(
        int ticketId, CancellationToken ct)
        => ToActionResult(await _attachments.ListAsync(ticketId, ct));

    /// <summary>Uploads a file against the ticket.</summary>
    /// <remarks>
    /// Multipart, not JSON — base64 in a JSON body inflates a file by a third and has to be
    /// held in memory to be parsed. <c>IFormFile</c> streams instead.
    /// </remarks>
    [HttpPost("{ticketId:int}/attachments")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024)]   // rejected by Kestrel before we read a byte
    [ProducesResponseType(typeof(AttachmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AttachmentDto>> UploadAttachment(
        int ticketId,
        IFormFile file,
        [FromForm] string? description,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }

        // IFormFile is converted to our own UploadedFile record here, at the boundary.
        // That is what keeps IFormFile — and therefore ASP.NET Core — out of the business
        // layer's signatures.
        await using var stream = file.OpenReadStream();

        var uploaded = new UploadedFile(file.FileName, file.ContentType, file.Length, stream);

        return ToActionResult(await _attachments.UploadAsync(ticketId, uploaded, description, ct));
    }

    /// <summary>Downloads an attachment.</summary>
    [HttpGet("{ticketId:int}/attachments/{attachmentId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DownloadAttachment(
        int ticketId, int attachmentId, CancellationToken ct)
    {
        var result = await _attachments.DownloadAsync(ticketId, attachmentId, ct);

        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }

        var (content, contentType, fileName) = result.Value;

        // File() streams the response rather than buffering the whole thing in memory,
        // and ASP.NET Core disposes the stream for us when the response completes.
        return File(content, contentType, fileName);
    }

    /// <summary>Removes an attachment record.</summary>
    [HttpDelete("{ticketId:int}/attachments/{attachmentId:int}")]
    [Authorize(Roles = AppRoles.Staff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteAttachment(int ticketId, int attachmentId, CancellationToken ct)
        => ToActionResult(await _attachments.DeleteAsync(ticketId, attachmentId, ct));

    // =====================================================================
    // Rating
    // =====================================================================

    /// <summary>The ticket's rating, if it has been rated.</summary>
    /// <remarks>
    /// 404 while the ticket is unrated, rather than 200 with a null body. "There is no rating
    /// resource at this URL" is precisely what has happened, and it is the answer that tells a
    /// front end to show the rating form — a null inside a 200 makes every caller write the
    /// same "is it really there?" check.
    /// </remarks>
    /// <response code="200">The rating.</response>
    /// <response code="404">The ticket is not visible to the caller, or has not been rated.</response>
    [HttpGet("{ticketId:int}/rating")]
    [ProducesResponseType(typeof(RatingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RatingDto>> GetRating(int ticketId, CancellationToken ct)
        => ToActionResult(await _ratings.GetAsync(ticketId, ct));

    /// <summary>Rates a resolved ticket. Only the person who reported it.</summary>
    [HttpPost("{ticketId:int}/rating")]
    [ProducesResponseType(typeof(RatingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RatingDto>> Rate(
        int ticketId, [FromBody] CreateRatingDto dto, CancellationToken ct)
        => ToActionResult(await _ratings.RateAsync(ticketId, dto, ct));
}

/// <summary>A concrete <see cref="PagedQuery"/> so the history endpoint can bind one.</summary>
/// <remarks>
/// <c>PagedQuery</c> is abstract and the model binder cannot instantiate an abstract type —
/// the same reason <c>UserQuery</c> exists in <c>SupportControllers</c>. History needs no
/// filters of its own beyond page and pageSize, so the subclass is empty and says so.
/// </remarks>
public class TicketHistoryQuery : PagedQuery;
