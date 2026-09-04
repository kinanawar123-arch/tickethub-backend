namespace TicketHub.Contracts.Abstractions;

/// <summary>
/// The four role names, as constants.
/// </summary>
/// <remarks>
/// WHY NOT JUST WRITE "Admin" IN THE ATTRIBUTE?
/// Because <c>[Authorize(Roles = "Admin")]</c> compiles perfectly with
/// <c>[Authorize(Roles = "Adnim")]</c> too — and that attribute does not throw, it just
/// silently denies everyone forever. A typo in a magic string is a security hole that no
/// compiler will catch. A typo in <c>AppRoles.Admni</c> is a red squiggle.
/// </remarks>
public static class AppRoles
{
    /// <summary>Full control, including user management and hard deletes.</summary>
    public const string Admin = "Admin";

    /// <summary>Runs a department: assigns tickets, sees everything inside their department.</summary>
    public const string Supervisor = "Supervisor";

    /// <summary>Works tickets inside their own department.</summary>
    public const string Agent = "Agent";

    /// <summary>A member of the public. Sees only the tickets they reported.</summary>
    public const string Citizen = "Citizen";

    /// <summary>Every role, for seeding.</summary>
    public static readonly string[] All = { Admin, Supervisor, Agent, Citizen };

    /// <summary>Shorthand for the common "staff only" check.</summary>
    public const string Staff = Admin + "," + Supervisor + "," + Agent;
}

/// <summary>
/// Custom claim types we put in the JWT beyond the standard ones.
/// </summary>
/// <remarks>
/// Putting <c>DepartmentId</c> in the token means the security filter costs zero database
/// round trips per request. The price is staleness: if someone is moved to another department
/// their old token keeps the old value until it expires. With a 15-minute access token that
/// window is acceptable — and it is exactly the trade-off that makes JWTs fast.
/// </remarks>
public static class AppClaimTypes
{
    public const string DepartmentId = "department_id";
    public const string AgentId = "agent_id";
    public const string UserType = "user_type";
    public const string DisplayName = "display_name";

    /// <summary>
    /// Copied from the Identity user. If it changes — password reset, forced logout —
    /// every token minted before the change stops validating. Our emergency brake.
    /// </summary>
    public const string SecurityStamp = "security_stamp";
}
