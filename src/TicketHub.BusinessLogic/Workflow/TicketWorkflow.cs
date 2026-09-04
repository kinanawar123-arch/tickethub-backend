using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Workflow;

namespace TicketHub.BusinessLogic.Workflow;

/// <summary>
/// Which status changes are legal. The business rules of the ticket life cycle, in one place.
/// </summary>
/// <remarks>
/// WHY A TABLE AND NOT A PILE OF <c>if</c> STATEMENTS IN THE SERVICE?
/// <list type="bullet">
/// <item>You can <em>read</em> it. A reviewer can check the rules against what the client
///       asked for in about ten seconds.</item>
/// <item>You can change it without touching the service. Adding "Cancelled can be re-opened"
///       is one array entry, not a new branch buried in a method.</item>
/// <item>It is trivially unit-testable: feed it every pair of statuses and assert.</item>
/// </list>
/// This is a small example of a much bigger idea — <b>represent rules as data</b>. The day
/// the municipality wants to configure its own workflow, this is a table in the database
/// instead of a rewrite.
/// </remarks>
public static class TicketWorkflow
{
    /// <summary>From → the statuses you are allowed to move to.</summary>
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Allowed = new()
    {
        [TicketStatus.Open] = new[]
        {
            TicketStatus.InProgress, TicketStatus.OnHold, TicketStatus.Cancelled
        },

        [TicketStatus.InProgress] = new[]
        {
            TicketStatus.OnHold, TicketStatus.Resolved, TicketStatus.Cancelled
        },

        [TicketStatus.OnHold] = new[]
        {
            TicketStatus.InProgress, TicketStatus.Cancelled
        },

        // Resolved is not the end: the reporter may say it is not actually fixed.
        [TicketStatus.Resolved] = new[]
        {
            TicketStatus.Closed, TicketStatus.InProgress
        },

        // Closed and Cancelled are terminal. An empty array is the rule, stated positively —
        // much clearer than the absence of a case in a switch.
        [TicketStatus.Closed] = Array.Empty<TicketStatus>(),
        [TicketStatus.Cancelled] = Array.Empty<TicketStatus>()
    };

    // =====================================================================
    // Reading the table from outside
    // =====================================================================
    //
    // The dictionary above stays private, and everything below is a read-only view of it.
    //
    // WHY NOT JUST MAKE IT PUBLIC? Because a public Dictionary<,> is writable by anyone who
    // can see it — one line in a controller could add a transition at runtime and the rules
    // would differ between two servers running the same build. A rule table that can be
    // edited from anywhere is no longer a rule table.

    /// <summary>Every status, in life-cycle order.</summary>
    /// <remarks>
    /// From <c>Enum.GetValues</c> rather than a hand-written list, so a new member of
    /// <see cref="TicketStatus"/> appears here without anyone remembering to add it.
    /// </remarks>
    public static IReadOnlyList<TicketStatus> AllStatuses { get; } = Enum.GetValues<TicketStatus>();

    /// <summary>The whole transition table: from → the statuses it may become.</summary>
    public static IReadOnlyDictionary<TicketStatus, IReadOnlyList<TicketStatus>> Transitions { get; }
        = Allowed.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<TicketStatus>)entry.Value);

    /// <summary>The legal destinations from one status. Empty for a terminal one.</summary>
    public static IReadOnlyList<TicketStatus> AllowedFrom(TicketStatus from)
        => Transitions.TryGetValue(from, out var targets) ? targets : Array.Empty<TicketStatus>();

    // ---------------------------------------------------------------------
    // The same table, in the shape the API publishes
    // ---------------------------------------------------------------------
    //
    // These projections live HERE, next to the rules, rather than in a controller. Two
    // reasons, and the second is the important one:
    //
    //   1. A controller that built these would be doing business mapping, which is exactly
    //      what controllers are not for in this project.
    //   2. There is then only ONE place that knows the workflow — so what the client is told
    //      and what the service enforces cannot drift apart. The day somebody adds
    //      "Cancelled can be re-opened" to the array above, the published map says so too,
    //      with no second edit to forget.
    //
    // Computed once, at type initialisation: the table never changes at runtime, so rebuilding
    // these lists on every request would be pure waste.

    /// <summary>Every status with its flags — what <c>GET /api/workflow/statuses</c> returns.</summary>
    public static IReadOnlyList<WorkflowStatusDto> Statuses { get; } = AllStatuses
        .Select(status => new WorkflowStatusDto
        {
            Status = status,
            Name = status.ToString(),
            IsTerminal = IsTerminal(status),
            IsResolution = IsResolution(status)
        })
        .ToArray();

    /// <summary>The full map — what <c>GET /api/workflow/transitions</c> returns.</summary>
    public static IReadOnlyList<WorkflowTransitionDto> TransitionMap { get; } = AllStatuses
        .Select(from => new WorkflowTransitionDto
        {
            From = from,
            FromName = from.ToString(),
            To = DescribeTargets(from)
        })
        .ToArray();

    /// <summary>The legal destinations from one status, each with its "reason required" flag.</summary>
    /// <remarks>
    /// Public because the per-ticket endpoint needs the same list: see
    /// <c>TicketService.GetWorkflowAsync</c>, which takes this and adds "…and may YOU do it".
    /// </remarks>
    public static IReadOnlyList<WorkflowTargetDto> DescribeTargets(TicketStatus from)
        => AllowedFrom(from)
            .Select(to => new WorkflowTargetDto
            {
                Status = to,
                Name = to.ToString(),
                RequiresReason = RequiresReason(to)
            })
            .ToArray();

    // =====================================================================
    // The rules themselves
    // =====================================================================

    public static bool CanTransition(TicketStatus from, TicketStatus to)
        => from != to && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>A message a human can act on, not "invalid transition".</summary>
    public static string DescribeInvalidTransition(TicketStatus from, TicketStatus to)
    {
        if (from == to)
        {
            return $"The ticket is already {from}.";
        }

        var targets = Allowed.TryGetValue(from, out var t) ? t : Array.Empty<TicketStatus>();

        return targets.Length == 0
            ? $"A {from} ticket is final and cannot be changed."
            : $"A {from} ticket cannot move to {to}. Allowed: {string.Join(", ", targets)}.";
    }

    /// <summary>True when reaching this status means the work is finished.</summary>
    public static bool IsResolution(TicketStatus status)
        => status is TicketStatus.Resolved or TicketStatus.Closed;

    /// <summary>True when nothing further can happen to the ticket.</summary>
    public static bool IsTerminal(TicketStatus status)
        => status is TicketStatus.Closed or TicketStatus.Cancelled;

    /// <summary>Cancelling without saying why leaves an audit trail nobody can use.</summary>
    public static bool RequiresReason(TicketStatus to)
        => to is TicketStatus.Cancelled or TicketStatus.OnHold;
}
