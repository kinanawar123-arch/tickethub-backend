using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Enums;

namespace TicketHub.DataAccess.Entities;

/// <summary>
/// The centre of the model: one reported problem, from "there is a hole in my street"
/// to "it is fixed".
/// </summary>
/// <remarks>
/// <see cref="IValidatableObject"/> is implemented at the bottom. Data annotations validate
/// one property at a time; this is where <em>cross-property</em> rules live — the ones that
/// need to see two fields at once.
/// </remarks>
public class Ticket : AuditableEntity, IValidatableObject
{
    public int Id { get; set; }

    /// <summary>
    /// The reference a citizen quotes on the phone: <c>TKT-2026-000123</c>.
    /// </summary>
    /// <remarks>
    /// Why not just use <see cref="Id"/>? Because the primary key is an implementation detail
    /// and this is a business identifier. It is generated in <c>TicketService</c>, it is
    /// unique (enforced by an index), and it can change format next year without touching a
    /// single foreign key.
    /// </remarks>
    public string TicketNumber { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    // ---------------------------------------------------------------------
    // Who reported it
    // ---------------------------------------------------------------------

    /// <summary>
    /// Free text. A phone operator genuinely does log tickets on behalf of callers, so this
    /// stays — but it is decoration, not identity.
    /// </summary>
    public string ReporterName { get; set; } = string.Empty;

    public string? ReporterPhone { get; set; }

    public string? ReporterEmail { get; set; }

    /// <summary>
    /// The real identity of the reporter. Set by the server from the JWT, never from the
    /// request body — see the warning on <c>CreateTicketDto</c>.
    /// </summary>
    /// <remarks>
    /// Nullable because a walk-in complaint recorded by an operator has no citizen account
    /// behind it.
    /// </remarks>
    public int? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }

    // ---------------------------------------------------------------------
    // Where
    // ---------------------------------------------------------------------

    public string? LocationAddress { get; set; }

    /// <summary>
    /// <c>decimal</c>, not <c>double</c>. Coordinates are exact values we store and compare,
    /// and binary floating point cannot represent most decimal fractions exactly — so
    /// <c>double</c> quietly drifts. Same reason money is never a double.
    /// </summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    // ---------------------------------------------------------------------
    // Relationships
    // ---------------------------------------------------------------------

    /// <summary>Required — every ticket has a category. A non-nullable FK is what says so.</summary>
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    /// <summary>
    /// Denormalised: you could reach the department through <c>Category.Department</c>.
    /// </summary>
    /// <remarks>
    /// WHY DUPLICATE IT? Because the department filter runs on <em>every single query in the
    /// application</em>. Reaching it through Category means every query joins Categories
    /// whether or not it needed category data, and the filter's correctness depends on that
    /// join being written correctly, every time, by everyone.
    /// <para/>
    /// A denormalised column makes it <c>t.DepartmentId == currentUser.DepartmentId</c> — one
    /// indexed comparison, no join, impossible to get subtly wrong. The cost is that we must
    /// keep it in sync when a ticket's category changes; that is one line in
    /// <c>TicketService.UpdateAsync</c>. Denormalising for a security filter is a normal,
    /// defensible trade.
    /// </remarks>
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    /// <summary>
    /// Optional — a ticket may sit unassigned in the queue.
    /// Required-vs-optional is decided by the nullability of the FK, and nothing else.
    /// </summary>
    public int? AssignedAgentId { get; set; }
    public Agent? AssignedAgent { get; set; }

    // ----- 1:N children -----

    public ICollection<TicketComment> Comments { get; set; } = new List<TicketComment>();
    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
    public ICollection<TicketHistory> History { get; set; } = new List<TicketHistory>();

    /// <summary>Optional 1:1 — a ticket is rated at most once, and only after it is resolved.</summary>
    public Rating? Rating { get; set; }

    /// <summary>The live-chat thread for this ticket, if one was ever opened.</summary>
    public Conversation? Conversation { get; set; }

    // ---------------------------------------------------------------------
    // Dates
    // ---------------------------------------------------------------------

    /// <summary>The SLA deadline, computed from the category's SlaHours when the ticket is created.</summary>
    public DateTime? DueAt { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // ---------------------------------------------------------------------
    // Concurrency
    // ---------------------------------------------------------------------

    /// <summary>
    /// Optimistic concurrency token. SQL Server maintains this 8-byte value itself and bumps
    /// it on every UPDATE.
    /// </summary>
    /// <remarks>
    /// EF puts the old value in the WHERE clause of every UPDATE and DELETE. If someone else
    /// changed the row in the meantime, zero rows match, and EF raises
    /// <c>DbUpdateConcurrencyException</c> instead of silently overwriting their work.
    /// <para/>
    /// The scenario this prevents: two supervisors open the same ticket, one sets it to
    /// Resolved, the other sets the priority to Urgent and saves a second later — and the
    /// first one's change vanishes with no error anywhere. Last-write-wins is a data-loss bug
    /// that no log will ever show you.
    /// </remarks>
    public byte[]? RowVersion { get; set; }

    // ---------------------------------------------------------------------
    // Cross-property validation
    // ---------------------------------------------------------------------

    /// <summary>
    /// Rules that need to see more than one property at a time.
    /// </summary>
    /// <remarks>
    /// <c>[Range]</c> can say "latitude is between -90 and 90". It cannot say "if you gave me
    /// a latitude you must also give me a longitude", because an attribute only ever sees the
    /// one property it is attached to. That is what this method is for.
    /// <para/>
    /// It runs automatically during model binding when the object being bound is a Ticket,
    /// and we also call it explicitly in the service — because the API is not the only caller.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Latitude.HasValue ^ Longitude.HasValue)
        {
            yield return new ValidationResult(
                "Latitude and longitude must be supplied together — half a coordinate points nowhere.",
                new[] { nameof(Latitude), nameof(Longitude) });
        }

        if (ResolvedAt.HasValue && ResolvedAt < CreatedAt)
        {
            yield return new ValidationResult(
                "A ticket cannot be resolved before it was created.",
                new[] { nameof(ResolvedAt) });
        }

        if (Status is TicketStatus.Resolved or TicketStatus.Closed && ResolvedAt is null)
        {
            yield return new ValidationResult(
                "A resolved or closed ticket must have a ResolvedAt timestamp.",
                new[] { nameof(ResolvedAt) });
        }

        if (Priority == TicketPriority.Urgent && string.IsNullOrWhiteSpace(LocationAddress))
        {
            yield return new ValidationResult(
                "An urgent ticket needs a location so a crew can be sent to it.",
                new[] { nameof(LocationAddress) });
        }
    }

    /// <summary>
    /// Past its SLA and not finished yet. A read-only helper, so no caller re-derives the rule.
    /// </summary>
    /// <remarks>
    /// <c>[NotMapped]</c> in the configuration — EF must not try to make a column out of it.
    /// And note that queries do NOT use this property: EF cannot translate a C# property
    /// getter into SQL, so a <c>Where(t =&gt; t.IsOverdue)</c> would throw
    /// "could not be translated". Query code repeats the expression; see TicketRepository.
    /// </remarks>
    public bool IsOverdue =>
        DueAt.HasValue
        && DueAt.Value < DateTime.UtcNow
        && Status != TicketStatus.Resolved
        && Status != TicketStatus.Closed
        && Status != TicketStatus.Cancelled;
}
