using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.BusinessLogic.Workflow;
using TicketHub.Contracts.Workflow;

namespace TicketHub.Api.Controllers;

/// <summary>
/// The ticket life cycle, published so a client can render it.
/// </summary>
/// <remarks>
/// WHY AN ENDPOINT FOR RULES THAT NEVER CHANGE AT RUNTIME?
/// <para/>
/// Because the alternative is a second copy of the transition table written in JavaScript. It
/// works on the day it is written and then drifts: somebody adds "Cancelled can be re-opened"
/// to <see cref="TicketWorkflow"/>, ships it, and the front end goes on hiding a button the
/// API would happily accept — or worse, offering one it refuses. Two copies of a rule is one
/// copy too many, and the one that is not enforced is the one that rots.
/// <para/>
/// <b>The shape of these two endpoints.</b> They describe the workflow itself, not any
/// ticket — no id in either route, and no database is touched to answer them. The per-ticket
/// question ("what may I do to <em>this</em> ticket, as <em>me</em>, right now?") is a
/// different resource and lives with the ticket:
/// <c>GET /api/tickets/{id}/workflow</c>.
/// <para/>
/// <b>Cache them.</b> The answers are identical for every caller and change only on deploy.
/// A client that fetches the map once at start-up and keeps it is doing the right thing.
/// <para/>
/// ⚠ NOTHING HERE IS A SECURITY CONTROL. Publishing the rules tells an honest client what to
/// draw; it does not stop a dishonest one from posting whatever it likes. Every transition is
/// checked again in <c>TicketService.ChangeStatusAsync</c>, which is the only place a rule is
/// actually enforced.
/// </remarks>
[Route("api/workflow")]
[Authorize]   // any signed-in role may read the map — a citizen needs it as much as an agent
[Produces("application/json")]
public class WorkflowController : ApiControllerBase
{
    // NO CONSTRUCTOR AND NO INJECTED SERVICE, which is unusual enough to be worth a line.
    // The workflow is a static table of business rules, not state — there is nothing to
    // fetch, nothing per-request, and nothing worth mocking in a test. Wrapping it in an
    // injected service to look consistent would add a layer that does nothing.
    //
    // The projections still live in the business layer (see TicketWorkflow), so this file
    // holds no rules of its own — which is the part that actually matters.

    /// <summary>Every status a ticket can be in, with its flags.</summary>
    /// <remarks>
    /// Includes the terminal ones. A client rendering a filter dropdown or a legend needs
    /// Closed and Cancelled in the list just as much as Open.
    /// </remarks>
    /// <response code="200">All six statuses.</response>
    [HttpGet("statuses")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowStatusDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WorkflowStatusDto>> Statuses()
        => Ok(TicketWorkflow.Statuses);

    /// <summary>The whole transition table: from each status, everywhere it may go.</summary>
    /// <remarks>
    /// A status with an empty <c>to</c> list is terminal. That is the rule stated positively —
    /// far clearer to a client than the status simply being absent from the map.
    /// </remarks>
    /// <response code="200">One entry per status.</response>
    [HttpGet("transitions")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowTransitionDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WorkflowTransitionDto>> Transitions()
        => Ok(TicketWorkflow.TransitionMap);
}
