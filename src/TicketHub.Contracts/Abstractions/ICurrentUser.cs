namespace TicketHub.Contracts.Abstractions;

/// <summary>
/// "Who is making this request?" — answered without any layer having to know about HTTP.
/// </summary>
/// <remarks>
/// The DbContext needs the caller's id to stamp <c>CreatedById</c>. The ticket service needs
/// their department to enforce the security filter. Neither should reach for
/// <c>IHttpContextAccessor</c>: the moment they do, they cannot run in a background job, a
/// console tool, or a unit test.
/// <para/>
/// So the lower layers depend on this small interface, and the API project provides the one
/// implementation that does know about HTTP. That inversion is the whole point of the
/// four-project layout — dependencies point inwards, towards the abstraction.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>The user's id, or null for anonymous callers and background jobs.</summary>
    int? UserId { get; }

    /// <summary>Their email / username, for audit messages.</summary>
    string? Email { get; }

    string? DisplayName { get; }

    /// <summary>True when the request carries a valid token.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The Identity roles from the token's claims.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Set for staff; null for citizens. Drives the department security filter.</summary>
    int? DepartmentId { get; }

    /// <summary>The caller's Agent row id, when they have one.</summary>
    int? AgentId { get; }

    bool IsInRole(string role);
}
