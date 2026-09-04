using TicketHub.Contracts.Common;
using TicketHub.Contracts.Tickets;
using TicketHub.Contracts.Workflow;

namespace TicketHub.BusinessLogic.Services;

/// <summary>
/// Everything you can do to a ticket, expressed without a single mention of HTTP.
/// </summary>
/// <remarks>
/// Read the signatures and notice what is absent: no <c>IActionResult</c>, no
/// <c>HttpContext</c>, no status codes. Every method takes DTOs and returns a
/// <see cref="ServiceResult{T}"/>.
/// <para/>
/// That is not stylistic. It is what lets the same <c>ResolveAsync</c> be called from the
/// controller, from a nightly job that auto-closes stale tickets, and from a unit test —
/// without any of them dragging ASP.NET Core along.
/// </remarks>
public interface ITicketService
{
    Task<PagedResult<TicketListItemDto>> SearchAsync(TicketQuery query, CancellationToken ct = default);

    /// <summary>
    /// The caller's own work: tickets assigned to them if they are staff, tickets they
    /// reported if they are a citizen.
    /// </summary>
    /// <remarks>
    /// "Mine" means something different depending on who is asking, and the answer comes from
    /// the token — so there is no user id in the signature for anyone to tamper with.
    /// </remarks>
    Task<PagedResult<TicketListItemDto>> GetMineAsync(TicketQuery query, CancellationToken ct = default);

    Task<ServiceResult<TicketDetailDto>> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Where the ticket is in the workflow, and which moves this caller may make from here.
    /// </summary>
    /// <remarks>
    /// Exists so a front end can render only the buttons that are legal right now. It is a
    /// convenience for the client, never the enforcement: the rules are checked again on the
    /// way in, because a hidden button stops nobody with a HTTP client.
    /// </remarks>
    Task<ServiceResult<TicketWorkflowDto>> GetWorkflowAsync(int id, CancellationToken ct = default);

    /// <summary>The audited timeline, newest first and paged.</summary>
    Task<ServiceResult<PagedResult<TicketHistoryDto>>> GetHistoryAsync(
        int id, PagedQuery query, CancellationToken ct = default);

    Task<ServiceResult<TicketDetailDto>> CreateAsync(CreateTicketDto dto, CancellationToken ct = default);

    Task<ServiceResult<TicketDetailDto>> UpdateAsync(int id, UpdateTicketDto dto, CancellationToken ct = default);

    Task<ServiceResult<TicketDetailDto>> ChangeStatusAsync(int id, ChangeTicketStatusDto dto, CancellationToken ct = default);

    /// <summary>
    /// Sends a Resolved ticket back to InProgress: "you closed it, it is not fixed".
    /// </summary>
    /// <remarks>
    /// Its own method rather than a status change, because it is the one transition a citizen
    /// is allowed to make — and because "re-open" is what the button says. The endpoint that
    /// changes status is staff-only; this one is not, and the reporter check lives in the
    /// service where it can see the row.
    /// </remarks>
    Task<ServiceResult<TicketDetailDto>> ReopenAsync(int id, CancellationToken ct = default);

    Task<ServiceResult<TicketDetailDto>> AssignAsync(int id, AssignTicketDto dto, CancellationToken ct = default);

    /// <summary>Picks the least-loaded agent in the ticket's department.</summary>
    Task<ServiceResult<TicketDetailDto>> AutoAssignAsync(int id, CancellationToken ct = default);

    /// <summary>Soft delete — the row stays, the global query filter hides it.</summary>
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);

    Task<ServiceResult<TicketStatisticsDto>> GetStatisticsAsync(CancellationToken ct = default);
}
