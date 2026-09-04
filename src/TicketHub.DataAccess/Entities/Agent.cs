namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A member of staff who resolves tickets.
/// </summary>
/// <remarks>
/// This is the domain side of a person. <see cref="ApplicationUser"/> is the authentication
/// side. See the long note on <c>AgentDto</c> for why they are two entities and not one.
/// </remarks>
public class Agent : AuditableEntity
{
    public int Id { get; set; }

    public string FullName { get; set; } = string.Empty;

    /// <summary>False = on leave or has left. Excluded from auto-assignment.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The workload cap the auto-assigner respects.</summary>
    public int MaxOpenTickets { get; set; } = 10;

    // ----- Required 1:1 with the login -----

    /// <summary>
    /// The Identity user this agent signs in as.
    /// </summary>
    /// <remarks>
    /// What makes a relationship one-to-one is <b>uniqueness on the foreign key</b>. An FK
    /// alone gives you one-to-<em>many</em>, because nothing stops two Agent rows carrying
    /// the same <c>UserId</c>. The unique index in <c>AgentConfiguration</c> is what makes
    /// it 1:1.
    /// </remarks>
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    // ----- Required 1:N with the department: the security boundary -----

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    // ----- Optional 1:1 with the profile -----

    /// <summary>
    /// The rarely-read columns, split into their own table. Nullable because an agent may
    /// not have filled it in yet.
    /// </summary>
    public AgentProfile? Profile { get; set; }

    // ----- N:N with skills -----

    /// <summary>
    /// An agent has many skills and a skill belongs to many agents. Two collections pointing
    /// at each other is all EF needs: it creates the <c>AgentSkills</c> join table itself,
    /// and there is no join entity to write — as long as the join carries no data of its own.
    /// The moment it needs, say, a proficiency level, you must write the join class by hand.
    /// </summary>
    public ICollection<Skill> Skills { get; set; } = new List<Skill>();

    // ----- 1:N -----

    public ICollection<Ticket> AssignedTickets { get; set; } = new List<Ticket>();
}

/// <summary>
/// The 1:1 extension of <see cref="Agent"/>.
/// </summary>
/// <remarks>
/// THE TRICK THAT MAKES A 1:1 ACTUALLY 1:1.
/// The cleanest way to guarantee uniqueness on the foreign key is to make the FK <em>be</em>
/// the primary key. <see cref="AgentId"/> is therefore both, and the database physically
/// cannot store two profiles for one agent.
/// <para/>
/// Why split at all? These columns are read on one screen and never in a list query. Keeping
/// them out of <c>Agents</c> keeps that table narrow, which means more rows fit in each page
/// of memory and every scan of it is cheaper.
/// </remarks>
public class AgentProfile : AuditableEntity
{
    /// <summary>Primary key AND foreign key. Configured in AgentProfileConfiguration.</summary>
    public int AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public string? Biography { get; set; }

    public string? AvatarUrl { get; set; }

    public string? OfficePhone { get; set; }

    public DateTime? HiredOn { get; set; }
}

/// <summary>A capability tag on an agent: "Electrical", "Arabic", "Heavy machinery".</summary>
public class Skill
{
    public int Id { get; set; }

    /// <summary>Unique, case-insensitively — otherwise you end up with "Electrical" and "electrical".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The other half of the N:N. Needed for "which agents can do X" queries.</summary>
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();

    // Skill deliberately does NOT inherit AuditableEntity. It is a small lookup list with no
    // interesting history — auditing it would be four columns of ceremony for no answer
    // anyone will ever ask for. Not every table needs every pattern.
}
