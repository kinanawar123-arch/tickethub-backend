using TicketHub.Contracts.Enums;

namespace TicketHub.Contracts.Workflow;

/// <summary>
/// The ticket life cycle, described to whoever has to draw it.
/// </summary>
/// <remarks>
/// WHY THE SERVER SHIPS ITS OWN RULES TO THE CLIENT.
/// <para/>
/// Without these DTOs a front end has exactly two options, and both are bad: hard-code a copy
/// of the transition table in JavaScript (which drifts out of step the first time the rules
/// change, and nobody notices until a user is told "Resolved → Closed is not allowed" by a
/// server that thinks otherwise), or show every button and let the API reject the illegal
/// ones (which teaches users that this application is broken).
/// <para/>
/// So the workflow is published. <see cref="WorkflowTransitionDto"/> is the whole map — static,
/// cacheable, the same for everybody. <see cref="TicketWorkflowDto"/> is the answer for one
/// ticket and one caller right now, which is what the detail screen actually needs to decide
/// which buttons exist.
/// <para/>
/// ⚠ THIS IS A CONVENIENCE, NOT A CONTROL. Every rule published here is enforced again in
/// <c>TicketService</c>, because anything the client is told is something the client can
/// ignore. A hidden button is not a security measure.
/// </remarks>
public class WorkflowStatusDto
{
    public TicketStatus Status { get; init; }

    /// <summary>The enum spelled out, so the front end needs no copy of the enum.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Nothing can follow this status — Closed and Cancelled.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>Reaching this status means the work is finished — Resolved and Closed.</summary>
    public bool IsResolution { get; init; }
}

/// <summary>One legal destination, and what it costs to get there.</summary>
public class WorkflowTargetDto
{
    public TicketStatus Status { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// True when the move is refused without a reason. The client should show the text box
    /// <em>before</em> the request, rather than after a 400 has already been returned.
    /// </summary>
    public bool RequiresReason { get; init; }
}

/// <summary>One row of the transition table: from a status, to each status it may become.</summary>
public class WorkflowTransitionDto
{
    public TicketStatus From { get; init; }
    public string FromName { get; init; } = string.Empty;

    /// <summary>Empty for a terminal status. That is the rule stated positively, not a gap.</summary>
    public IReadOnlyList<WorkflowTargetDto> To { get; init; } = Array.Empty<WorkflowTargetDto>();
}

/// <summary>
/// Where one ticket is, and what <em>this</em> caller may do with it next.
/// </summary>
/// <remarks>
/// The difference from <see cref="WorkflowTransitionDto"/> is the word "caller". The map says
/// a Resolved ticket may go back to InProgress; this says whether <em>you</em> may be the one
/// to move it. Citizens may re-open the ticket they reported and nothing else, so the same
/// ticket produces a different answer for the agent and for the reporter looking at it.
/// </remarks>
public class TicketWorkflowDto
{
    public int TicketId { get; init; }

    public TicketStatus CurrentStatus { get; init; }
    public string CurrentStatusName { get; init; } = string.Empty;

    public bool IsTerminal { get; init; }

    /// <summary>
    /// Every transition the workflow allows from the current status — including the ones this
    /// caller may not make.
    /// </summary>
    /// <remarks>
    /// Sending the refused ones too, flagged, rather than silently omitting them: a screen can
    /// then grey a button out and explain, which is a far better experience than a button that
    /// mysteriously is not there. The client chooses how to render it; the server states facts.
    /// </remarks>
    public IReadOnlyList<AllowedTransitionDto> AllowedTransitions { get; init; }
        = Array.Empty<AllowedTransitionDto>();
}

/// <summary>One button on the ticket detail screen, and whether it should be enabled.</summary>
public class AllowedTransitionDto
{
    public TicketStatus Status { get; init; }
    public string Name { get; init; } = string.Empty;

    public bool RequiresReason { get; init; }

    /// <summary>False when the workflow allows this move but the caller's role does not.</summary>
    public bool IsPermitted { get; init; }
}
