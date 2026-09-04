using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Common;

namespace TicketHub.Contracts.Departments;

/// <summary>What we send back for a department.</summary>
/// <remarks>
/// READ THIS FIRST — WHY DTOs AT ALL?
/// <list type="number">
/// <item>An entity is shaped for the database. A DTO is shaped for the caller. They are
///       different jobs, and gluing them together means every schema change is a breaking
///       API change.</item>
/// <item>Entities have navigation properties. Serialising one straight to JSON either
///       loops forever (Ticket → Comments → Ticket → ...) or quietly drags half the database
///       across the wire.</item>
/// <item>Security. <c>PasswordHash</c> is a property on the user entity. It must never be
///       a property on anything a controller returns. If it is not on the DTO, it cannot leak.</item>
/// </list>
/// </remarks>
public class DepartmentDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ContactEmail { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Counts, not the rows themselves — see <c>DepartmentRepository</c> for how.</summary>
    public int CategoryCount { get; init; }
    public int AgentCount { get; init; }
    public int OpenTicketCount { get; init; }

    public AuditInfoDto Audit { get; init; } = new();
}

/// <summary>The body of <c>POST /api/departments</c>.</summary>
/// <remarks>
/// Notice what is NOT here: no <c>Id</c> (the database assigns it), no <c>CreatedAt</c>
/// (the DbContext fills it), no <c>CreatedById</c> (that comes from the token — see the
/// mass-assignment warning in AuthService). A create DTO should contain exactly the fields
/// a human is allowed to type, and nothing else.
/// </remarks>
public class CreateDepartmentDto
{
    [Required]
    [StringLength(80, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(300)]
    public string? Description { get; set; }

    [EmailAddress]
    [StringLength(160)]
    public string? ContactEmail { get; set; }
}

/// <summary>
/// The body of <c>PUT /api/departments/{id}</c>.
/// </summary>
/// <remarks>
/// It looks like a copy of the create DTO plus <c>IsActive</c>, and a trainee will
/// reasonably ask why we do not reuse one class. Because they diverge. Within a few sprints
/// the create form gains a field the edit form must not touch (or the reverse), and un-picking
/// one shared class at that point is worse than the small duplication now.
/// </remarks>
public class UpdateDepartmentDto
{
    [Required]
    [StringLength(80, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(300)]
    public string? Description { get; set; }

    [EmailAddress]
    [StringLength(160)]
    public string? ContactEmail { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Query string for <c>GET /api/departments</c>.</summary>
public class DepartmentQuery : PagedQuery
{
    /// <summary>Null = both. True = only active. False = only retired.</summary>
    public bool? IsActive { get; set; }
}
