using Microsoft.EntityFrameworkCore;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Tickets;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Repositories;

/// <summary>
/// Who is allowed to see which tickets, expressed as data instead of as an <c>if</c> ladder
/// repeated in every method.
/// </summary>
/// <remarks>
/// The service builds one of these from the token and hands it to the repository. The
/// repository applies it to <b>every</b> query, with no exceptions and no "just this once".
/// <para/>
/// Why a struct passed in, rather than the repository reading the current user itself? Because
/// then a background job — the nightly SLA sweep, a report — can ask for
/// <see cref="Everything"/> explicitly and legibly, instead of the repository silently
/// behaving differently depending on whether an HTTP request happens to exist.
/// </remarks>
public readonly record struct TicketAccessFilter(
    bool SeeEverything,
    int? DepartmentId,
    int? CreatedByUserId)
{
    /// <summary>Admins, and internal jobs that must not be filtered.</summary>
    public static TicketAccessFilter Everything => new(true, null, null);

    /// <summary>Staff: everything inside their own department, and nothing outside it.</summary>
    public static TicketAccessFilter ForDepartment(int departmentId) => new(false, departmentId, null);

    /// <summary>A citizen: only the tickets they reported themselves.</summary>
    public static TicketAccessFilter ForReporter(int userId) => new(false, null, userId);
}

public interface ITicketRepository : IRepository<Ticket>
{
    /// <summary>A filtered, sorted, paged list — projected straight to the list DTO.</summary>
    Task<PagedResult<TicketListItemDto>> SearchAsync(
        TicketQuery query, TicketAccessFilter access, CancellationToken ct = default);

    /// <summary>One ticket with everything the detail screen shows, in as few queries as possible.</summary>
    Task<TicketDetailDto?> GetDetailAsync(
        int id, TicketAccessFilter access, bool includeInternalComments, CancellationToken ct = default);

    /// <summary>The tracked entity, for the write path. Null if the caller may not touch it.</summary>
    Task<Ticket?> GetForUpdateAsync(int id, TicketAccessFilter access, CancellationToken ct = default);

    /// <summary>One page of a ticket's timeline, newest first.</summary>
    /// <remarks>
    /// NO ACCESS FILTER HERE, AND THAT IS DELIBERATE — the same shape as
    /// <c>CommentRepository.GetForTicketAsync</c>. The caller establishes that the <em>ticket</em>
    /// is visible first, and then asks for its children; a filter on the child rows would be a
    /// second copy of the same rule, which is one copy too many.
    /// <para/>
    /// <see cref="TicketDetailDto.History"/> already carries the timeline for the detail screen.
    /// This exists for the ticket that has been through forty status changes, where "all of it,
    /// every time you open the page" stops being reasonable.
    /// </remarks>
    Task<PagedResult<TicketHistoryDto>> GetHistoryAsync(
        int ticketId, PagedQuery query, CancellationToken ct = default);

    Task<bool> TicketNumberExistsAsync(string ticketNumber, CancellationToken ct = default);

    /// <summary>Highest sequence used this year, so the next TicketNumber can continue from it.</summary>
    Task<int> GetMaxSequenceForYearAsync(int year, CancellationToken ct = default);

    Task<TicketStatisticsDto> GetStatisticsAsync(TicketAccessFilter access, CancellationToken ct = default);

    /// <summary>How many open tickets an agent is carrying. Used by the workload cap.</summary>
    Task<int> CountOpenForAgentAsync(int agentId, CancellationToken ct = default);

    /// <summary>
    /// Tells EF what the client believed the row looked like, so the concurrency check has
    /// something to compare against.
    /// </summary>
    /// <remarks>
    /// Without this, EF compares against the value it read a moment ago — which is of course
    /// current, so the check always passes and concurrency detection does nothing. Setting
    /// the ORIGINAL value to what the client was shown is what makes the WHERE clause say
    /// "…AND RowVersion = &lt;what they had on screen&gt;".
    /// <para/>
    /// It lives in the repository because <c>ChangeTracker</c> is a data-layer concept; the
    /// service should not be reaching into EF internals.
    /// </remarks>
    void SetOriginalRowVersion(Ticket ticket, byte[] rowVersion);
}

public class TicketRepository : Repository<Ticket>, ITicketRepository
{
    public TicketRepository(TicketHubDbContext db) : base(db) { }

    // =====================================================================
    // The list query — the single most instructive method in the project
    // =====================================================================

    public async Task<PagedResult<TicketListItemDto>> SearchAsync(
        TicketQuery query, TicketAccessFilter access, CancellationToken ct = default)
    {
        // STEP 1 — start from an IQueryable. Nothing has executed yet.
        //
        // Everything below is building an expression tree, not fetching data. The database
        // is not touched until CountAsync/ToListAsync at the bottom, which means all of this
        // collapses into ONE SELECT with one WHERE and one ORDER BY.
        //
        // The mistake this avoids: calling .ToList() early and then filtering in memory.
        // That pulls the whole table across the network and does the database's job in C#.
        var tickets = Db.Tickets.AsNoTracking();

        // STEP 2 — security first, always.
        //
        // Applying the access filter before anything else means there is no code path where
        // a later condition can widen what the caller is allowed to see.
        tickets = ApplyAccessFilter(tickets, access);

        // STEP 3 — the optional filters. Each one is only added if it was supplied.
        //
        // This is why the query object uses nullable types: null means "the caller did not
        // ask", which is a different thing from "the caller asked for zero".
        if (query.Status.HasValue)
        {
            tickets = tickets.Where(t => t.Status == query.Status.Value);
        }

        if (query.Priority.HasValue)
        {
            tickets = tickets.Where(t => t.Priority == query.Priority.Value);
        }

        if (query.CategoryId.HasValue)
        {
            tickets = tickets.Where(t => t.CategoryId == query.CategoryId.Value);
        }

        if (query.DepartmentId.HasValue)
        {
            tickets = tickets.Where(t => t.DepartmentId == query.DepartmentId.Value);
        }

        if (query.AssignedAgentId.HasValue)
        {
            tickets = tickets.Where(t => t.AssignedAgentId == query.AssignedAgentId.Value);
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssignedAgentId == null);
        }

        if (query.CreatedFrom.HasValue)
        {
            // SARGABLE: the column is left bare on one side of the comparison, so SQL Server
            // can seek straight into IX_Tickets_CreatedAt.
            //
            // The version that kills performance is a function ON the column —
            //   Where(t => t.CreatedAt.Date >= from)
            // — because the index is built on CreatedAt, not on DATE(CreatedAt), so the
            // engine has to compute the expression for every row before it can compare.
            // A full scan on a large table, from one innocent-looking .Date.
            tickets = tickets.Where(t => t.CreatedAt >= query.CreatedFrom.Value);
        }

        if (query.CreatedTo.HasValue)
        {
            // Half-open range: >= start AND < end-of-day. The alternative, <= end-of-day,
            // needs you to pick 23:59:59 (loses the last second) or 23:59:59.9999999 (works
            // until someone changes the column precision). Half-open is always right.
            var exclusiveEnd = query.CreatedTo.Value.Date.AddDays(1);
            tickets = tickets.Where(t => t.CreatedAt < exclusiveEnd);
        }

        if (query.Overdue == true)
        {
            // NOTE: we spell the condition out rather than using Ticket.IsOverdue.
            // EF cannot translate a C# property getter into SQL — it would throw
            // "could not be translated". A computed property is for C#; a query needs an
            // expression EF can read.
            var now = DateTime.UtcNow;
            tickets = tickets.Where(t =>
                t.DueAt != null &&
                t.DueAt < now &&
                t.Status != TicketStatus.Resolved &&
                t.Status != TicketStatus.Closed &&
                t.Status != TicketStatus.Cancelled);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            // EF.Functions.Like translates to SQL LIKE and runs in the database.
            //
            // Be honest about the cost: a leading wildcard ('%term%') cannot use a normal
            // index, so this is a scan. It is fine at our size. If this table reaches
            // millions of rows the answer is SQL Server full-text search, not a cleverer LIKE.
            tickets = tickets.Where(t =>
                EF.Functions.Like(t.Title, $"%{term}%") ||
                EF.Functions.Like(t.TicketNumber, $"%{term}%") ||
                EF.Functions.Like(t.ReporterName, $"%{term}%"));
        }

        // STEP 4 — count BEFORE paging.
        //
        // We want the total number of matches, not the size of one page. Note this runs
        // against the filtered query but before Skip/Take, and it is a second round trip —
        // unavoidable if you want a page count.
        var totalCount = await tickets.CountAsync(ct);

        if (totalCount == 0)
        {
            // Nothing matched, so do not bother sending a second query for zero rows.
            return PagedResult<TicketListItemDto>.Empty(query.Page, query.PageSize);
        }

        // STEP 5 — sort. Always with a tie-breaker.
        tickets = ApplySort(tickets, query);

        // STEP 6 — page, then project.
        //
        // Select() AFTER Skip/Take, so the projection only runs on the rows we are keeping.
        //
        // And note what the projection does: no Include anywhere. CategoryName comes through
        // the navigation property, and CommentCount becomes a SQL COUNT sub-select. EF turns
        // this into a SELECT of exactly these columns — it never loads a Category object or
        // a single comment row. That is the difference between a fast list endpoint and a
        // slow one.
        var now2 = DateTime.UtcNow;

        var items = await tickets
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(t => new TicketListItemDto
            {
                Id = t.Id,
                TicketNumber = t.TicketNumber,
                Title = t.Title,
                Status = t.Status,
                StatusName = t.Status.ToString(),
                Priority = t.Priority,
                PriorityName = t.Priority.ToString(),
                CategoryName = t.Category.Name,
                DepartmentName = t.Department.Name,
                AssignedAgentName = t.AssignedAgent != null ? t.AssignedAgent.FullName : null,
                CommentCount = t.Comments.Count,
                AttachmentCount = t.Attachments.Count,
                CreatedAt = t.CreatedAt,
                DueAt = t.DueAt,
                IsOverdue = t.DueAt != null
                            && t.DueAt < now2
                            && t.Status != TicketStatus.Resolved
                            && t.Status != TicketStatus.Closed
                            && t.Status != TicketStatus.Cancelled
            })
            .ToListAsync(ct);

        return new PagedResult<TicketListItemDto>(items, totalCount, query.Page, query.PageSize);
    }

    // =====================================================================
    // The detail query
    // =====================================================================

    public async Task<TicketDetailDto?> GetDetailAsync(
        int id, TicketAccessFilter access, bool includeInternalComments, CancellationToken ct = default)
    {
        var query = ApplyAccessFilter(Db.Tickets.AsNoTracking(), access);

        var now = DateTime.UtcNow;

        // One query, projected. The child collections are filtered and ordered INSIDE the
        // projection, which EF turns into sub-selects — so "only the public comments,
        // newest first" happens in SQL rather than by loading everything and filtering in C#.
        //
        // Note especially the comment filter: if a citizen must not see internal notes, those
        // rows must not be in the result set. Hiding them in the front end means the JSON
        // still went over the wire, and anyone can open the network tab.
        return await query
            .Where(t => t.Id == id)
            .Select(t => new TicketDetailDto
            {
                Id = t.Id,
                TicketNumber = t.TicketNumber,
                Title = t.Title,
                Description = t.Description,
                Status = t.Status,
                StatusName = t.Status.ToString(),
                Priority = t.Priority,
                PriorityName = t.Priority.ToString(),

                CategoryId = t.CategoryId,
                CategoryName = t.Category.Name,
                DepartmentId = t.DepartmentId,
                DepartmentName = t.Department.Name,

                AssignedAgentId = t.AssignedAgentId,
                AssignedAgentName = t.AssignedAgent != null ? t.AssignedAgent.FullName : null,

                CreatedByUserId = t.CreatedByUserId,
                CreatedByUserName = t.CreatedByUser != null ? t.CreatedByUser.DisplayName : null,

                ReporterName = t.ReporterName,
                ReporterPhone = t.ReporterPhone,
                ReporterEmail = t.ReporterEmail,

                LocationAddress = t.LocationAddress,
                Latitude = t.Latitude,
                Longitude = t.Longitude,

                DueAt = t.DueAt,
                ResolvedAt = t.ResolvedAt,
                ClosedAt = t.ClosedAt,
                IsOverdue = t.DueAt != null
                            && t.DueAt < now
                            && t.Status != TicketStatus.Resolved
                            && t.Status != TicketStatus.Closed
                            && t.Status != TicketStatus.Cancelled,

                Comments = t.Comments
                    .Where(c => includeInternalComments || !c.IsInternal)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new Contracts.Comments.CommentDto
                    {
                        Id = c.Id,
                        TicketId = c.TicketId,
                        Body = c.Body,
                        IsInternal = c.IsInternal,
                        AuthorId = c.AuthorId,
                        AuthorName = c.AuthorNameSnapshot,
                        IsFromAgent = t.AssignedAgent != null && c.AuthorId == t.AssignedAgent.UserId,
                        CreatedAt = c.CreatedAt,
                        UpdatedAt = c.UpdatedAt
                    })
                    .ToList(),

                Attachments = t.Attachments
                    .OrderBy(a => a.CreatedAt)
                    .Select(a => new Contracts.Attachments.AttachmentDto
                    {
                        Id = a.Id,
                        TicketId = a.TicketId,
                        FileName = a.FileName,
                        ContentType = a.ContentType,
                        SizeBytes = a.SizeBytes,
                        DownloadUrl = $"/api/tickets/{a.TicketId}/attachments/{a.Id}",
                        UploadedById = a.UploadedById,
                        UploadedByName = a.UploadedBy != null ? a.UploadedBy.DisplayName : null,
                        CreatedAt = a.CreatedAt
                    })
                    .ToList(),

                History = t.History
                    .OrderByDescending(h => h.ChangedAt)
                    .Select(h => new TicketHistoryDto
                    {
                        Id = h.Id,
                        Field = h.Field,
                        OldValue = h.OldValue,
                        NewValue = h.NewValue,
                        Note = h.Note,
                        ChangedAt = h.ChangedAt,
                        ChangedByName = h.ChangedByName
                    })
                    .ToList(),

                Rating = t.Rating == null ? null : new Contracts.Ratings.RatingDto
                {
                    Id = t.Rating.Id,
                    TicketId = t.Rating.TicketId,
                    Stars = t.Rating.Stars,
                    Comment = t.Rating.Comment,
                    CreatedAt = t.Rating.CreatedAt,
                    RatedByName = t.Rating.RatedByUser != null ? t.Rating.RatedByUser.DisplayName : null
                },

                Audit = new AuditInfoDto
                {
                    CreatedAt = t.CreatedAt,
                    CreatedById = t.CreatedById,
                    UpdatedAt = t.UpdatedAt,
                    UpdatedById = t.UpdatedById
                },

                // rowversion is 8 raw bytes; base64 so it survives a JSON round trip.
                RowVersion = t.RowVersion == null ? null : Convert.ToBase64String(t.RowVersion)
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The write path: a <b>tracked</b> entity, so changing a property is enough to save it.
    /// </summary>
    /// <remarks>
    /// Deliberately no <c>AsNoTracking()</c> here. This is the classic "my update saves
    /// nothing" bug: you load with AsNoTracking, change a property, call SaveChanges, get 0
    /// back and no error anywhere. EF was not tracking the object, so it had nothing to
    /// write. Reads get AsNoTracking; writes do not.
    /// </remarks>
    public async Task<Ticket?> GetForUpdateAsync(int id, TicketAccessFilter access, CancellationToken ct = default)
        => await ApplyAccessFilter(Db.Tickets, access).FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<PagedResult<TicketHistoryDto>> GetHistoryAsync(
        int ticketId, PagedQuery query, CancellationToken ct = default)
    {
        var history = Db.TicketHistories.AsNoTracking().Where(h => h.TicketId == ticketId);

        var total = await history.CountAsync(ct);

        var items = await history
            // Newest first, with Id as the tie-breaker. Two history rows written in the same
            // SaveChanges share a ChangedAt to the tick — a status change and a priority change
            // in one edit, for instance — so without the second key their order is whatever
            // SQL Server feels like, and paging can show one row twice and skip another.
            .OrderByDescending(h => h.ChangedAt)
            .ThenByDescending(h => h.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(h => new TicketHistoryDto
            {
                Id = h.Id,
                Field = h.Field,
                OldValue = h.OldValue,
                NewValue = h.NewValue,
                Note = h.Note,
                ChangedAt = h.ChangedAt,
                ChangedByName = h.ChangedByName
            })
            .ToListAsync(ct);

        return new PagedResult<TicketHistoryDto>(items, total, query.Page, query.PageSize);
    }

    public Task<bool> TicketNumberExistsAsync(string ticketNumber, CancellationToken ct = default)
        // IgnoreQueryFilters: a soft-deleted ticket still occupies its number, because the
        // unique index does not care that we consider the row deleted. Without this, the
        // check passes and the INSERT fails on the index — a 500 instead of a clean retry.
        => Db.Tickets.IgnoreQueryFilters().AnyAsync(t => t.TicketNumber == ticketNumber, ct);

    public async Task<int> GetMaxSequenceForYearAsync(int year, CancellationToken ct = default)
    {
        var prefix = $"TKT-{year}-";

        // No leading wildcard here, so this one CAN use IX_Tickets_TicketNumber:
        // a prefix search is a range seek.
        var numbers = await Db.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.TicketNumber.StartsWith(prefix))
            .Select(t => t.TicketNumber)
            .ToListAsync(ct);

        // The parsing happens in memory, on purpose: SQL Server has no clean way to say
        // "the integer after the second dash", and the set is one year of one municipality's
        // tickets. Know when to stop pushing work into SQL.
        var max = 0;
        foreach (var number in numbers)
        {
            var tail = number[prefix.Length..];
            if (int.TryParse(tail, out var value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }

    // =====================================================================
    // Dashboard numbers
    // =====================================================================

    public async Task<TicketStatisticsDto> GetStatisticsAsync(
        TicketAccessFilter access, CancellationToken ct = default)
    {
        var tickets = ApplyAccessFilter(Db.Tickets.AsNoTracking(), access);
        var now = DateTime.UtcNow;

        // GROUP BY status, projected to key + count. One round trip, and not a single
        // ticket row leaves the database.
        //
        // What you CANNOT do — and every trainee tries it once:
        //   GroupBy(t => t.Status).Select(g => new { g.Key, Rows = g.ToList() })
        // SQL GROUP BY returns aggregates per group, not the rows behind them. There is no
        // SQL for "the group key AND all its rows" in one grouped query, so EF either refuses
        // or quietly loads the whole table and groups in memory.
        var byStatus = await tickets
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var overdue = await tickets.CountAsync(t =>
            t.DueAt != null && t.DueAt < now &&
            t.Status != TicketStatus.Resolved &&
            t.Status != TicketStatus.Closed &&
            t.Status != TicketStatus.Cancelled, ct);

        var unassigned = await tickets.CountAsync(t => t.AssignedAgentId == null, ct);

        // AVG in SQL. The alternative — load every resolved ticket and average in C# —
        // transfers thousands of rows to compute one number.
        //
        // TWO DETAILS THAT LOOK FUSSY AND ARE NOT:
        //
        // 1. The projection is cast to `double?`, not `double`. SQL's AVG over zero rows
        //    returns NULL, and a non-nullable AverageAsync would throw trying to read it.
        //    Making the element type nullable lets the null come back and `?? 0` handle it.
        //    (The obvious-looking .DefaultIfEmpty(0) does NOT work here — EF cannot translate
        //    it in this position and fails with "could not be translated" at runtime.)
        //
        // 2. DateDiffMinute, then divide by 60 — rather than DateDiffHour, which truncates
        //    to whole hours before averaging and quietly loses up to 59 minutes per ticket.
        var avgHours = await tickets
            .Where(t => t.ResolvedAt != null)
            .Select(t => (double?)(EF.Functions.DateDiffMinute(t.CreatedAt, t.ResolvedAt!.Value) / 60.0))
            .AverageAsync(ct) ?? 0;

        // Projected off Departments, so no ticket rows are loaded here either.
        var byDepartment = await Db.Departments
            .AsNoTracking()
            .Select(d => new DepartmentTicketSummaryDto
            {
                DepartmentId = d.Id,
                DepartmentName = d.Name,
                Total = d.Tickets.Count,
                Open = d.Tickets.Count(t => t.Status == TicketStatus.Open),
                Resolved = d.Tickets.Count(t => t.Status == TicketStatus.Resolved),
                Urgent = d.Tickets.Count(t => t.Priority == TicketPriority.Urgent)
            })
            .OrderByDescending(d => d.Total)
            .ToListAsync(ct);

        int CountOf(TicketStatus status) =>
            byStatus.FirstOrDefault(s => s.Status == status)?.Count ?? 0;

        return new TicketStatisticsDto
        {
            Total = byStatus.Sum(s => s.Count),
            Open = CountOf(TicketStatus.Open),
            InProgress = CountOf(TicketStatus.InProgress),
            OnHold = CountOf(TicketStatus.OnHold),
            Resolved = CountOf(TicketStatus.Resolved),
            Closed = CountOf(TicketStatus.Closed),
            Cancelled = CountOf(TicketStatus.Cancelled),
            Overdue = overdue,
            Unassigned = unassigned,
            AverageResolutionHours = Math.Round(avgHours, 1),
            ByDepartment = byDepartment
        };
    }

    public void SetOriginalRowVersion(Ticket ticket, byte[] rowVersion)
        => Db.Entry(ticket).Property(t => t.RowVersion).OriginalValue = rowVersion;

    public Task<int> CountOpenForAgentAsync(int agentId, CancellationToken ct = default)
        => Db.Tickets.AsNoTracking().CountAsync(t =>
            t.AssignedAgentId == agentId &&
            (t.Status == TicketStatus.Open ||
             t.Status == TicketStatus.InProgress ||
             t.Status == TicketStatus.OnHold), ct);

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// The security filter. One indexed comparison, no join — this is what
    /// <c>Ticket.DepartmentId</c> is denormalised for.
    /// </summary>
    private static IQueryable<Ticket> ApplyAccessFilter(IQueryable<Ticket> query, TicketAccessFilter access)
    {
        if (access.SeeEverything)
        {
            return query;
        }

        if (access.DepartmentId.HasValue)
        {
            return query.Where(t => t.DepartmentId == access.DepartmentId.Value);
        }

        if (access.CreatedByUserId.HasValue)
        {
            return query.Where(t => t.CreatedByUserId == access.CreatedByUserId.Value);
        }

        // Fail CLOSED. If the filter says nothing, the caller sees nothing.
        //
        // The alternative — returning the unfiltered query when no rule matched — means that
        // the day someone adds a new user type and forgets a case, that user type silently
        // gets access to everything. Default deny; a missing rule should be an empty screen,
        // not a data breach.
        return query.Where(_ => false);
    }

    private static IQueryable<Ticket> ApplySort(IQueryable<Ticket> query, TicketQuery q)
    {
        // WHY EVERY SORT HAS A SECOND KEY.
        //
        // Rows with an identical CreatedAt have no guaranteed order in SQL — the engine may
        // return them differently on each run. With Skip/Take that means the same ticket can
        // appear on page 1 and page 2 while another is skipped entirely. Id is unique, so
        // adding it makes the order total and paging stable.
        var descending = q.SortDescending;

        return (q.SortBy?.ToLowerInvariant()) switch
        {
            "priority" => descending
                ? query.OrderByDescending(t => t.Priority).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Priority).ThenBy(t => t.Id),

            "status" => descending
                ? query.OrderByDescending(t => t.Status).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Status).ThenBy(t => t.Id),

            "dueat" => descending
                ? query.OrderByDescending(t => t.DueAt).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.DueAt).ThenBy(t => t.Id),

            "title" => descending
                ? query.OrderByDescending(t => t.Title).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Title).ThenBy(t => t.Id),

            // Unknown values fall through to the default rather than throwing. A typo in a
            // query string should not be a 500 — but note we never build SQL from the string
            // itself, which is what makes this safe from injection.
            _ => descending
                ? query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
        };
    }
}
