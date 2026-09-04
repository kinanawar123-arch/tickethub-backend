using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Common;

namespace TicketHub.Contracts.Agents;

/// <summary>A member of staff who resolves tickets.</summary>
/// <remarks>
/// WHY IS THIS SEPARATE FROM THE USER?
/// The tempting shortcut is to bolt <c>MaxOpenTickets</c> and <c>DepartmentId</c> onto the
/// Identity user and be done in five minutes. Resist it, for a reason that arrives within a
/// month: not everyone who logs in is an agent. Citizens log in to track their own tickets;
/// auditors want read-only access; a nightly integration needs a service account. None of
/// them are agents, and all of them need credentials.
/// <para/>
/// So: <b>the ability to log in</b> is an authentication concern (the Identity user), and
/// <b>the role someone plays in the business</b> is a domain concern (this). They are 1:1
/// today and will not stay that way.
/// </remarks>
public class AgentDto
{
    public int Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsActive { get; init; }

    public int DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;

    /// <summary>Above this many open tickets, the auto-assigner skips them.</summary>
    public int MaxOpenTickets { get; init; }

    /// <summary>How many they are carrying right now — a COUNT, not loaded rows.</summary>
    public int OpenTicketCount { get; init; }

    /// <summary>Simple derived flag so the UI does not re-implement the rule.</summary>
    public bool HasCapacity => OpenTicketCount < MaxOpenTickets;

    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();

    public AgentProfileDto? Profile { get; init; }

    public AuditInfoDto Audit { get; init; } = new();
}

/// <summary>The 1:1 extension of an agent — the fields most queries do not need.</summary>
/// <remarks>
/// Splitting rarely-read columns into their own table keeps the hot <c>Agents</c> table
/// narrow, which means more rows per page of memory and faster scans. The 1:1 is created by
/// making the FK <em>be</em> the primary key: nothing else stops two profile rows pointing
/// at the same agent.
/// </remarks>
public class AgentProfileDto
{
    public string? Biography { get; init; }
    public string? AvatarUrl { get; init; }
    public string? OfficePhone { get; init; }
    public DateTime? HiredOn { get; init; }
}

public class CreateAgentDto
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>The Identity user this agent logs in with. One user, one agent.</summary>
    [Required, Range(1, int.MaxValue)]
    public int UserId { get; set; }

    [Required, Range(1, int.MaxValue)]
    public int DepartmentId { get; set; }

    [Range(1, 200)]
    public int MaxOpenTickets { get; set; } = 10;

    /// <summary>Skill names. Existing ones are reused; unknown ones are created.</summary>
    public List<string> Skills { get; set; } = new();
}

public class UpdateAgentDto
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string FullName { get; set; } = string.Empty;

    [Required, Range(1, int.MaxValue)]
    public int DepartmentId { get; set; }

    [Range(1, 200)]
    public int MaxOpenTickets { get; set; } = 10;

    public bool IsActive { get; set; } = true;

    public List<string> Skills { get; set; } = new();
}

public class UpdateAgentProfileDto
{
    [StringLength(1000)]
    public string? Biography { get; set; }

    [Url, StringLength(300)]
    public string? AvatarUrl { get; set; }

    [Phone, StringLength(30)]
    public string? OfficePhone { get; set; }

    public DateTime? HiredOn { get; set; }
}

public class AgentQuery : PagedQuery
{
    public int? DepartmentId { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>True = only agents currently under their ticket cap.</summary>
    public bool? HasCapacity { get; set; }

    /// <summary>Filter to agents holding this skill.</summary>
    public string? Skill { get; set; }
}

/// <summary>A skill tag, e.g. "Electrical", "Arabic", "Heavy machinery".</summary>
public class SkillDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int AgentCount { get; init; }
}
