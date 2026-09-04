using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Attachments;
using TicketHub.Contracts.Comments;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Ratings;

namespace TicketHub.Contracts.Tickets;

/// <summary>
/// One row in a ticket list. Small on purpose.
/// </summary>
/// <remarks>
/// THE LIST/DETAIL SPLIT — the single most useful DTO habit you can pick up.
/// A list of 50 tickets does not need every description, every comment and every attachment;
/// it needs what fits in a table row. <see cref="TicketDetailDto"/> is the heavy one, and it
/// is only ever built for a single ticket the user actually opened.
/// <para/>
/// Get this wrong and your list endpoint pulls megabytes to render a table. It is the most
/// common cause of "the API is slow" in projects this size.
/// </remarks>
public class TicketListItemDto
{
    public int Id { get; init; }

    /// <summary>Human-facing reference like <c>TKT-2026-000123</c>. What the citizen quotes on the phone.</summary>
    public string TicketNumber { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public TicketStatus Status { get; init; }

    /// <summary>The enum spelled out, so the front end does not need its own copy of the enum.</summary>
    public string StatusName { get; init; } = string.Empty;

    public TicketPriority Priority { get; init; }
    public string PriorityName { get; init; } = string.Empty;

    public string CategoryName { get; init; } = string.Empty;
    public string DepartmentName { get; init; } = string.Empty;

    /// <summary>Null when nobody has picked the ticket up yet.</summary>
    public string? AssignedAgentName { get; init; }

    /// <summary>A count, not the comment rows. See the projection in TicketRepository.</summary>
    public int CommentCount { get; init; }
    public int AttachmentCount { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? DueAt { get; init; }

    /// <summary>Computed server-side so every client agrees on what "late" means.</summary>
    public bool IsOverdue { get; init; }
}

/// <summary>Everything about one ticket, for the detail screen.</summary>
public class TicketDetailDto
{
    public int Id { get; init; }
    public string TicketNumber { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public TicketStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public TicketPriority Priority { get; init; }
    public string PriorityName { get; init; } = string.Empty;

    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public int DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;

    public int? AssignedAgentId { get; init; }
    public string? AssignedAgentName { get; init; }

    /// <summary>Who filed it. Comes from the token at creation time, never from the request body.</summary>
    public int? CreatedByUserId { get; init; }
    public string? CreatedByUserName { get; init; }

    /// <summary>Free-text name of whoever phoned it in. Decoration, not identity.</summary>
    public string ReporterName { get; init; } = string.Empty;
    public string? ReporterPhone { get; init; }
    public string? ReporterEmail { get; init; }

    public string? LocationAddress { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }

    public DateTime? DueAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public bool IsOverdue { get; init; }

    public IReadOnlyList<CommentDto> Comments { get; init; } = Array.Empty<CommentDto>();
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = Array.Empty<AttachmentDto>();
    public IReadOnlyList<TicketHistoryDto> History { get; init; } = Array.Empty<TicketHistoryDto>();
    public RatingDto? Rating { get; init; }

    public AuditInfoDto Audit { get; init; } = new();

    /// <summary>
    /// The concurrency token, base64-encoded so it survives JSON.
    /// The client sends it back on update; if the row changed in between we return 409
    /// instead of silently overwriting somebody's work.
    /// </summary>
    public string? RowVersion { get; init; }
}

public class CreateTicketDto
{
    [Required]
    [StringLength(120, MinimumLength = 5,
        ErrorMessage = "Title must be between 5 and 120 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(4000, MinimumLength = 10)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "Pick a category.")]
    public int CategoryId { get; set; }

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    [Required]
    [StringLength(80, MinimumLength = 2)]
    public string ReporterName { get; set; } = string.Empty;

    [Phone]
    [StringLength(30)]
    public string? ReporterPhone { get; set; }

    [EmailAddress]
    [StringLength(160)]
    public string? ReporterEmail { get; set; }

    [StringLength(250)]
    public string? LocationAddress { get; set; }

    [Range(-90, 90)]
    public decimal? Latitude { get; set; }

    [Range(-180, 180)]
    public decimal? Longitude { get; set; }

    // ⚠ THERE IS DELIBERATELY NO CreatedByUserId HERE.
    // If it existed, any caller could create a ticket "as" anyone else by editing one
    // number in Postman. That is mass assignment / broken object level authorization,
    // and it sits near the top of the OWASP API Top 10 every year.
    // Anything that describes WHO THE CALLER IS comes from the token. If it is not on
    // the DTO, it cannot be forged.

    // Nor is there a Status: a new ticket is always Open. Letting a caller choose the
    // starting state means letting them skip the workflow.
}

public class UpdateTicketDto
{
    [Required]
    [StringLength(120, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(4000, MinimumLength = 10)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Range(1, int.MaxValue)]
    public int CategoryId { get; set; }

    public TicketPriority Priority { get; set; }

    [StringLength(250)]
    public string? LocationAddress { get; set; }

    [Range(-90, 90)]
    public decimal? Latitude { get; set; }

    [Range(-180, 180)]
    public decimal? Longitude { get; set; }

    /// <summary>
    /// The value from the GET, sent back untouched. Optional — omit it and you get
    /// last-write-wins. Send it and you get a 409 when someone else edited the row first.
    /// </summary>
    public string? RowVersion { get; set; }
}

/// <summary>Body of <c>PATCH /api/tickets/{id}/status</c>.</summary>
/// <remarks>
/// A status change is its own endpoint rather than a field on <see cref="UpdateTicketDto"/>,
/// because it is its own operation: it has its own rules (see <c>TicketWorkflow</c>), it writes
/// a history row, and it fires a notification. "Change one field" and "move through the
/// workflow" only look the same from the database's point of view.
/// </remarks>
public class ChangeTicketStatusDto
{
    [Required]
    public TicketStatus NewStatus { get; set; }

    /// <summary>Why. Required when cancelling — an audit trail with no reason is half an audit trail.</summary>
    [StringLength(500)]
    public string? Reason { get; set; }
}

/// <summary>Body of <c>PATCH /api/tickets/{id}/assign</c>.</summary>
public class AssignTicketDto
{
    /// <summary>Pass <c>null</c> to un-assign and put the ticket back in the queue.</summary>
    public int? AgentId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

/// <summary>
/// Everything <c>GET /api/tickets</c> accepts. Every filter is optional.
/// </summary>
/// <remarks>
/// The repository applies only the filters that were actually supplied — each one is an
/// <c>if (x is not null) query = query.Where(...)</c> on a composable <c>IQueryable</c>.
/// Nothing hits the database until <c>ToListAsync()</c>, so all of this becomes one SELECT.
/// </remarks>
public class TicketQuery : PagedQuery
{
    public TicketStatus? Status { get; set; }
    public TicketPriority? Priority { get; set; }
    public int? CategoryId { get; set; }
    public int? DepartmentId { get; set; }
    public int? AssignedAgentId { get; set; }

    /// <summary>True = only tickets nobody has picked up.</summary>
    public bool? Unassigned { get; set; }

    /// <summary>True = past its SLA due date and not yet resolved.</summary>
    public bool? Overdue { get; set; }

    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }

    /// <summary>One of: createdAt, priority, status, dueAt, title. Anything else falls back to createdAt.</summary>
    public string? SortBy { get; set; } = "createdAt";

    public bool SortDescending { get; set; } = true;
}

/// <summary>A single audited change to a ticket, for the detail page's timeline.</summary>
public class TicketHistoryDto
{
    public int Id { get; init; }
    public string Field { get; init; } = string.Empty;
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? Note { get; init; }
    public DateTime ChangedAt { get; init; }
    public string? ChangedByName { get; init; }
}

/// <summary>Numbers for the dashboard. Every one of them is a SQL aggregate — no rows loaded.</summary>
public class TicketStatisticsDto
{
    public int Total { get; init; }
    public int Open { get; init; }
    public int InProgress { get; init; }
    public int OnHold { get; init; }
    public int Resolved { get; init; }
    public int Closed { get; init; }
    public int Cancelled { get; init; }
    public int Overdue { get; init; }
    public int Unassigned { get; init; }
    public double AverageResolutionHours { get; init; }
    public IReadOnlyList<DepartmentTicketSummaryDto> ByDepartment { get; init; } = Array.Empty<DepartmentTicketSummaryDto>();
}

public class DepartmentTicketSummaryDto
{
    public int DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Open { get; init; }
    public int Resolved { get; init; }
    public int Urgent { get; init; }
}
