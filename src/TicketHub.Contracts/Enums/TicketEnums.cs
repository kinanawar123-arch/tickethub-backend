namespace TicketHub.Contracts.Enums;

/// <summary>
/// Where a ticket is in its life cycle.
/// </summary>
/// <remarks>
/// WHY AN ENUM AND NOT A TABLE?
/// The rule we use in this project: if the C# code branches on the value, it is an enum.
/// Our workflow really does say <c>if (status == Resolved) ...</c>, and adding a new status
/// would mean writing new code anyway — so a table would buy us nothing.
/// <para/>
/// <see cref="Category"/> and <see cref="Department"/> are the opposite case: the
/// municipality wants to add and rename them without a deployment, so they are tables.
/// <para/>
/// NOTE FOR TRAINEES: the numbers are written out explicitly on purpose. The enum is stored
/// in the database as an <c>int</c>. If someone reorders the members later and the numbers
/// are implicit, every row in the database silently changes meaning. Pin the numbers.
/// </remarks>
public enum TicketStatus
{
    /// <summary>Reported, nobody has picked it up yet.</summary>
    Open = 0,

    /// <summary>An agent is actively working on it.</summary>
    InProgress = 1,

    /// <summary>Waiting on the reporter or on a third party; the clock is paused.</summary>
    OnHold = 2,

    /// <summary>Work is finished. The reporter can still re-open or rate it.</summary>
    Resolved = 3,

    /// <summary>Finished and confirmed. Terminal state — nothing follows this.</summary>
    Closed = 4,

    /// <summary>Withdrawn, duplicate, or not a real issue. Also terminal.</summary>
    Cancelled = 5
}

/// <summary>How urgent the ticket is. Drives sorting and the SLA due date.</summary>
public enum TicketPriority
{
    Low = 0,
    Medium = 1,
    High = 2,

    /// <summary>Danger to people or property — burst main, live cable, blocked road.</summary>
    Urgent = 3
}

/// <summary>
/// What kind of person is behind a login. Identity handles *authentication*
/// (are you who you say you are); this handles the *domain* question (what are you to us).
/// </summary>
/// <remarks>
/// Keep this separate from Identity roles. A role answers "what may you do";
/// this answers "what are you". They line up today and will drift apart within a year —
/// that is normal, and the reason they are two different things.
/// </remarks>
public enum UserType
{
    /// <summary>A member of the public who reports problems.</summary>
    Citizen = 0,

    /// <summary>A municipality employee who resolves tickets. Has an <c>Agent</c> row.</summary>
    Employee = 1,

    /// <summary>A non-human login used by an integration or a nightly job.</summary>
    ServiceAccount = 2
}
