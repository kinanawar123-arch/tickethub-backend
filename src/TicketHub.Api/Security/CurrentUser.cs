using System.Security.Claims;
using TicketHub.Contracts.Abstractions;

namespace TicketHub.Api.Security;

/// <summary>
/// The one class in the solution that turns an HTTP request into "who is calling".
/// </summary>
/// <remarks>
/// This is the concrete side of the inversion described on <see cref="ICurrentUser"/>. The
/// data layer and the business layer depend on the <em>interface</em>; this implementation —
/// the only thing that knows <c>HttpContext</c> exists — lives up here in the API project,
/// where HTTP is allowed.
/// <para/>
/// Everything is a property that reads a claim on demand rather than a field captured in the
/// constructor. That matters because this is registered as Scoped and resolved during
/// request setup: capturing the user in the constructor can happen before authentication has
/// run, and you get a mysteriously anonymous caller on a perfectly valid token.
/// </remarks>
public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public int? UserId
    {
        get
        {
            var raw = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email)
                            ?? Principal?.FindFirstValue(ClaimTypes.Name);

    public string? DisplayName => Principal?.FindFirstValue(AppClaimTypes.DisplayName) ?? Email;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>
    /// The department from the token — which is why the security filter costs no queries.
    /// </summary>
    /// <remarks>
    /// The trade to be aware of: move someone to another department and their current token
    /// keeps the old value until it expires. With a 15-minute access token that window is
    /// acceptable. If it were not, you would look the department up per request and pay a
    /// database round trip for every single call.
    /// </remarks>
    public int? DepartmentId
    {
        get
        {
            var raw = Principal?.FindFirstValue(AppClaimTypes.DepartmentId);
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public int? AgentId
    {
        get
        {
            var raw = Principal?.FindFirstValue(AppClaimTypes.AgentId);
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}
