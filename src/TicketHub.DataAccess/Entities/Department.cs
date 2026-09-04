namespace TicketHub.DataAccess.Entities;

/// <summary>
/// An organisational unit that owns categories of work: Roads, Sanitation, Lighting, Parks.
/// </summary>
/// <remarks>
/// WHY A TABLE AND NOT AN ENUM?
/// The rule that holds up: if your <em>code branches on the value</em> it is an enum, because
/// adding a value means writing code anyway. If the business just wants to add, rename or
/// retire entries, it is a table. Nothing in our C# says <c>if (department == Roads)</c>, but
/// the municipality absolutely will want to add "Parks &amp; Green Spaces" on a Tuesday
/// afternoon without a deployment. So: table.
/// <para/>
/// Departments are also the security boundary. An Agent in Roads cannot read a Sanitation
/// ticket, and that rule is enforced in <c>TicketRepository</c> on every single query.
/// </remarks>
public class Department : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Unique — enforced by an index, not by an <c>if</c> in C#. See DepartmentConfiguration.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Where overflow / escalation mail goes.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>False = retired. Existing tickets keep pointing here; no new ones are accepted.</summary>
    public bool IsActive { get; set; } = true;

    // ----- Navigations: the "one" side of two 1:N relationships -----

    public ICollection<Category> Categories { get; set; } = new List<Category>();
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();

    /// <summary>
    /// Tickets are linked to their department directly as well as through their category.
    /// That duplication is deliberate — see the comment on <see cref="Ticket.DepartmentId"/>.
    /// </summary>
    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();
}
