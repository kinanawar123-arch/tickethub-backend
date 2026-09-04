using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Auth;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Notifications;
using TicketHub.Contracts.Ratings;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.Api.Controllers;

/// <summary>The bell icon.</summary>
/// <remarks>
/// Every endpoint here is implicitly "mine". There is no route that takes a user id, because
/// the answer always comes from the token — which means there is no id for anyone to tamper
/// with, and no authorization check to forget.
/// </remarks>
[Route("api/notifications")]
[Authorize]
[Produces("application/json")]
public class NotificationsController : ApiControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    /// <summary>The caller's notifications, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<NotificationDto>>> GetMine(
        [FromQuery] NotificationQuery query, CancellationToken ct)
        => Ok(await _notifications.GetMineAsync(query, ct));

    /// <summary>Just the unread count — for the badge.</summary>
    /// <remarks>
    /// Its own endpoint because the badge is polled far more often than the list is opened,
    /// and a COUNT is dramatically cheaper than fetching rows just to call
    /// <c>.length</c> on them.
    /// </remarks>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnreadCountDto>> UnreadCount(CancellationToken ct)
        => Ok(await _notifications.GetUnreadCountAsync(ct));

    [HttpPost("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MarkRead(int id, CancellationToken ct)
        => ToActionResult(await _notifications.MarkReadAsync(id, ct));

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MarkAllRead(CancellationToken ct)
        => ToActionResult(await _notifications.MarkAllReadAsync(ct));
}

/// <summary>Admin-only user management.</summary>
[Route("api/users")]
[Authorize(Roles = AppRoles.Admin)]
[Produces("application/json")]
public class UsersController : ApiControllerBase
{
    private readonly IUserService _users;

    public UsersController(IUserService users) => _users = users;

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<UserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserDto>>> Search(
        [FromQuery] UserQuery query, CancellationToken ct)
        => Ok(await _users.SearchAsync(query, ct));

    [HttpGet("{id:int}", Name = nameof(GetUser))]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetUser(int id, CancellationToken ct)
        => ToActionResult(await _users.GetByIdAsync(id, ct));

    /// <summary>Creates a staff login, and its Agent row when the user is an employee.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserDto dto, CancellationToken ct)
    {
        var result = await _users.CreateAsync(dto, ct);
        return CreatedResult(result, nameof(GetUser), new { id = result.Value?.Id });
    }

    /// <summary>Updates a user's details and roles.</summary>
    /// <remarks>
    /// A role change bumps the security stamp, which invalidates every token that user is
    /// holding right now. Their next request has to refresh, and the new token carries the
    /// new roles — so a demotion takes effect immediately rather than in fifteen minutes.
    /// </remarks>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> Update(
        int id, [FromBody] UpdateUserDto dto, CancellationToken ct)
        => ToActionResult(await _users.UpdateAsync(id, dto, ct));

    /// <summary>Deactivates an account. There is deliberately no delete.</summary>
    [HttpPost("{id:int}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Deactivate(int id, CancellationToken ct)
        => ToActionResult(await _users.DeactivateAsync(id, ct));
}

/// <summary>A concrete <see cref="PagedQuery"/> so the user list can bind one.</summary>
/// <remarks>
/// <c>PagedQuery</c> is abstract, and the model binder cannot instantiate an abstract type.
/// This tiny subclass exists purely to give it something to create — the kind of small
/// concession that is worth making explicit rather than leaving somebody to puzzle over.
/// </remarks>
public class UserQuery : PagedQuery;

/// <summary>Reports and analytics.</summary>
[Route("api/reports")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Supervisor}")]
[Produces("application/json")]
public class ReportsController : ApiControllerBase
{
    private readonly IRatingService _ratings;

    public ReportsController(IRatingService ratings) => _ratings = ratings;

    /// <summary>Average satisfaction per category, worst-served last.</summary>
    /// <remarks>
    /// One grouped, aggregated, sorted and paged query — see
    /// <c>ReportRepository.GetCategorySatisfactionAsync</c>. Not a single Rating row is loaded
    /// into memory to produce it.
    /// </remarks>
    [HttpGet("category-satisfaction")]
    [ProducesResponseType(typeof(PagedResult<CategorySatisfactionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CategorySatisfactionDto>>> CategorySatisfaction(
        [FromQuery] UserQuery query, CancellationToken ct)
        => Ok(await _ratings.GetCategorySatisfactionAsync(query, ct));

    /// <summary>Tickets created and resolved per day, for the trend chart.</summary>
    [HttpGet("daily-volume")]
    [ProducesResponseType(typeof(IReadOnlyList<DailyTicketCountDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DailyTicketCountDto>>> DailyVolume(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? departmentId,
        CancellationToken ct)
    {
        // Sensible defaults, so the endpoint is usable with no parameters at all — and, more
        // importantly, so nobody can accidentally ask for every ticket since the year 1.
        var start = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var end = to ?? DateTime.UtcNow.Date.AddDays(1);

        if (end <= start)
        {
            return BadRequest(new { error = "'to' must be after 'from'." });
        }

        if ((end - start).TotalDays > 366)
        {
            return BadRequest(new { error = "The range cannot exceed one year." });
        }

        return Ok(await _ratings.GetDailyVolumeAsync(start, end, departmentId, ct));
    }
}
