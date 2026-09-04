using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.BusinessLogic.Workflow;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Notifications;
using TicketHub.Contracts.Tickets;
using TicketHub.Contracts.Workflow;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

/// <summary>
/// The ticket use cases. This is where "what the business means" lives.
/// </summary>
/// <remarks>
/// WHAT BELONGS IN A SERVICE, AND WHAT DOES NOT.
/// <list type="bullet">
/// <item><b>In</b>: rules ("you cannot resolve a cancelled ticket"), orchestration across
///       repositories, mapping entity → DTO, deciding what to notify.</item>
/// <item><b>Out</b>: SQL and LINQ (that is the repository's job), and status codes,
///       route values and model binding (the controller's).</item>
/// </list>
/// The test for whether something is in the right place: could this method run unchanged
/// from a console application? If not, something HTTP-shaped has leaked in.
/// </remarks>
public class TicketService : ITicketService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<TicketService> _log;

    public TicketService(
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IRealtimeNotifier notifier,
        ILogger<TicketService> log)
    {
        _uow = uow;
        _currentUser = currentUser;
        _notifier = notifier;
        _log = log;
    }

    // =====================================================================
    // Reads
    // =====================================================================

    public Task<PagedResult<TicketListItemDto>> SearchAsync(TicketQuery query, CancellationToken ct = default)
        => _uow.Tickets.SearchAsync(query, BuildAccessFilter(), ct);

    /// <summary>
    /// "My tickets" — which means two different queries depending on who is asking.
    /// </summary>
    /// <remarks>
    /// For staff it is <b>the work on my desk</b>: assigned to me, inside my department.
    /// For a citizen it is <b>what I reported</b>. Same endpoint, same DTO, different question —
    /// and the answer is decided here rather than by a parameter, so nobody can ask for
    /// somebody else's desk.
    /// <para/>
    /// Note there is no new repository method. Both cases are the existing search with a
    /// different filter, and an endpoint that can be expressed with what exists should be.
    /// </remarks>
    public Task<PagedResult<TicketListItemDto>> GetMineAsync(
        TicketQuery query, CancellationToken ct = default)
    {
        if (IsStaff() && _currentUser.AgentId.HasValue)
        {
            // Overwriting a filter the caller supplied, on purpose. ?assignedAgentId=7 on this
            // endpoint is not a request we honour — "mine" is not negotiable. Same reasoning as
            // CommentService forcing IncludeInternal to false: the request is an input, not an
            // instruction.
            query.AssignedAgentId = _currentUser.AgentId;

            // Still intersected with the department filter. The agent id alone would be enough
            // in practice, but security filters are not something to leave out because the
            // other condition "probably" covers it.
            return _uow.Tickets.SearchAsync(query, BuildAccessFilter(), ct);
        }

        if (_currentUser.UserId.HasValue)
        {
            // ForReporter, not BuildAccessFilter(). An Admin's normal filter is Everything, and
            // "my tickets" must not quietly mean "all tickets" for them — this endpoint answers
            // one question and it is the same question for everybody.
            return _uow.Tickets.SearchAsync(
                query, TicketAccessFilter.ForReporter(_currentUser.UserId.Value), ct);
        }

        // Anonymous. Nothing is theirs.
        return Task.FromResult(PagedResult<TicketListItemDto>.Empty(query.Page, query.PageSize));
    }

    public async Task<ServiceResult<TicketDetailDto>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetDetailAsync(
            id,
            BuildAccessFilter(),
            includeInternalComments: IsStaff(),
            ct);

        // NOTE THE 404 FOR "you are not allowed to see this".
        //
        // The access filter is part of the query, so a ticket the caller may not see simply
        // does not come back — and we cannot distinguish "does not exist" from "not yours".
        // That is the right answer anyway: replying 403 would confirm that ticket 4711
        // exists, which is information the caller has not earned. 404 tells them nothing.
        return ticket is null
            ? ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.")
            : ServiceResult<TicketDetailDto>.Success(ticket);
    }

    public async Task<ServiceResult<TicketStatisticsDto>> GetStatisticsAsync(CancellationToken ct = default)
        => ServiceResult<TicketStatisticsDto>.Success(
            await _uow.Tickets.GetStatisticsAsync(BuildAccessFilter(), ct));

    /// <summary>
    /// What this caller may do to this ticket next.
    /// </summary>
    /// <remarks>
    /// TWO QUESTIONS, ANSWERED IN ONE PLACE.
    /// <list type="number">
    /// <item><b>What does the workflow allow?</b> — <see cref="TicketWorkflow"/> answers that,
    ///       and it is the same table <see cref="ChangeStatusAsync"/> checks against.</item>
    /// <item><b>May you be the one to do it?</b> — <see cref="MayTransition"/> answers that,
    ///       and it is the same method <see cref="ReopenAsync"/> checks against.</item>
    /// </list>
    /// Neither answer is re-derived here. If this method invented its own rules, the front end
    /// would eventually offer a button the API refuses — which is worse than no button at all,
    /// because the user has already decided the system works before it tells them it does not.
    /// </remarks>
    public async Task<ServiceResult<TicketWorkflowDto>> GetWorkflowAsync(
        int id, CancellationToken ct = default)
    {
        // Through GetByIdAsync, so the access filter and the 404-not-403 decision are made in
        // exactly one place. See the long note there for why a ticket you may not see is
        // "not found" rather than "forbidden".
        var found = await GetByIdAsync(id, ct);
        if (!found.IsSuccess || found.Value is null)
        {
            return ServiceResult<TicketWorkflowDto>.NotFound($"Ticket {id} was not found.");
        }

        var ticket = found.Value;

        var transitions = TicketWorkflow.DescribeTargets(ticket.Status)
            .Select(target => new AllowedTransitionDto
            {
                Status = target.Status,
                Name = target.Name,
                RequiresReason = target.RequiresReason,
                IsPermitted = MayTransition(ticket.Status, target.Status, ticket.CreatedByUserId)
            })
            .ToArray();

        return ServiceResult<TicketWorkflowDto>.Success(new TicketWorkflowDto
        {
            TicketId = ticket.Id,
            CurrentStatus = ticket.Status,
            CurrentStatusName = ticket.StatusName,
            IsTerminal = TicketWorkflow.IsTerminal(ticket.Status),
            AllowedTransitions = transitions
        });
    }

    public async Task<ServiceResult<PagedResult<TicketHistoryDto>>> GetHistoryAsync(
        int id, PagedQuery query, CancellationToken ct = default)
    {
        // The parent is the boundary, not the route. Establish that the caller may see the
        // TICKET before handing them its timeline — the same order as
        // CommentService.GetForTicketAsync, and for the same reason.
        var ticket = await _uow.Tickets.GetDetailAsync(id, BuildAccessFilter(), IsStaff(), ct);
        if (ticket is null)
        {
            return ServiceResult<PagedResult<TicketHistoryDto>>.NotFound($"Ticket {id} was not found.");
        }

        return ServiceResult<PagedResult<TicketHistoryDto>>.Success(
            await _uow.Tickets.GetHistoryAsync(id, query, ct));
    }

    // =====================================================================
    // Create
    // =====================================================================

    public async Task<ServiceResult<TicketDetailDto>> CreateAsync(
        CreateTicketDto dto, CancellationToken ct = default)
    {
        // 1. Validate the things the DTO's attributes could not.
        //
        // AN ANONYMOUS REPORT NEEDS A WAY BACK TO THE PERSON WHO MADE IT.
        //
        // POST /api/tickets is [AllowAnonymous], because a citizen who has to create an
        // account first mostly does not report the pothole at all. The price of that is a
        // ticket with no CreatedByUserId: there is no account to notify, and no "my tickets"
        // list for them to look at later. The contact details ARE the channel, so when nobody
        // is signed in they stop being optional.
        //
        // This cannot be a [Required] attribute on the DTO, because the very same DTO is used
        // by signed-in callers — a phone operator logging a call already knows who they are,
        // and their token carries it. A rule that depends on WHO is calling has to be checked
        // where the caller is known, which is here.
        if (_currentUser.UserId is null)
        {
            var missingContact = ValidateAnonymousReporter(dto);
            if (missingContact is not null)
            {
                return ServiceResult<TicketDetailDto>.Invalid(missingContact);
            }
        }

        // [Required] on CategoryId proves a number arrived. It cannot prove the number is a
        // real, active category — that needs the database, which is why this check is here
        // and not in an attribute.
        var routing = await _uow.Categories.GetRoutingInfoAsync(dto.CategoryId, ct);
        if (routing is null)
        {
            return ServiceResult<TicketDetailDto>.Invalid(
                $"Category {dto.CategoryId} does not exist or is not active.");
        }

        var (departmentId, slaHours) = routing.Value;
        var now = DateTime.UtcNow;

        // 2. Build the entity.
        var ticket = new Ticket
        {
            TicketNumber = await GenerateTicketNumberAsync(now.Year, ct),
            Title = dto.Title.Trim(),
            Description = dto.Description.Trim(),
            Priority = dto.Priority,

            // Status is NOT taken from the DTO. A new ticket is always Open — letting the
            // caller choose the starting state means letting them skip the workflow.
            Status = TicketStatus.Open,

            CategoryId = dto.CategoryId,

            // Denormalised from the category, so the security filter never needs a join.
            DepartmentId = departmentId,

            ReporterName = dto.ReporterName.Trim(),
            ReporterPhone = dto.ReporterPhone,
            ReporterEmail = dto.ReporterEmail,

            // ⚠ FROM THE TOKEN, NOT FROM THE BODY. This one line is the difference between
            // an audit trail and a fiction. See the warning on CreateTicketDto.
            //
            // Null for an anonymous report, and the column has always been nullable for
            // exactly this case — a walk-in complaint logged by an operator has no citizen
            // account behind it either. Nothing else in the method needs to know.
            CreatedByUserId = _currentUser.UserId,

            LocationAddress = dto.LocationAddress?.Trim(),
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,

            // The SLA is computed once, here, and stored. If the category's SlaHours changes
            // next year, tickets already in flight keep the promise that was made when they
            // were filed.
            DueAt = now.AddHours(slaHours)
        };

        // 3. Cross-property rules that need the finished object.
        var validation = ValidateEntity(ticket);
        if (validation is not null)
        {
            return ServiceResult<TicketDetailDto>.Invalid(validation);
        }

        // 4. The opening history entry, so the timeline starts at the beginning.
        ticket.History.Add(new TicketHistory
        {
            Field = "Status",
            NewValue = TicketStatus.Open.ToString(),
            Note = "Ticket created.",
            ChangedAt = now,
            ChangedById = _currentUser.UserId,

            // Falls back to the name they typed when nobody is signed in, so the timeline
            // starts with "Reported by Anna Petrova" rather than a blank line. It is unverified
            // and treated as such — decoration, not identity. ChangedById is still null, and
            // that is the field anything trustworthy is built on.
            ChangedByName = _currentUser.DisplayName ?? ticket.ReporterName
        });

        await _uow.Tickets.AddAsync(ticket, ct);

        // 5. ONE save. The ticket and its history row land together or not at all —
        // that is what the unit of work is for.
        await _uow.SaveChangesAsync(ct);

        _log.LogInformation("Ticket {TicketNumber} created by user {UserId}",
            ticket.TicketNumber, _currentUser.UserId);

        // 6. Urgent work should not sit in a queue waiting to be noticed.
        if (ticket.Priority == TicketPriority.Urgent)
        {
            await NotifyDepartmentAsync(ticket, "Urgent ticket reported",
                $"{ticket.TicketNumber}: {ticket.Title}", ct);
        }

        return await ReloadAsync(ticket.Id, ct);
    }

    // =====================================================================
    // Update
    // =====================================================================

    public async Task<ServiceResult<TicketDetailDto>> UpdateAsync(
        int id, UpdateTicketDto dto, CancellationToken ct = default)
    {
        // TRACKED, not AsNoTracking. This is the write path: EF has to be watching the object
        // for a property assignment to become an UPDATE. Load it with AsNoTracking and
        // SaveChanges returns 0 with no error whatsoever — the single most confusing
        // half-hour in EF, and it looks completely correct while it happens.
        var ticket = await _uow.Tickets.GetForUpdateAsync(id, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.");
        }

        if (TicketWorkflow.IsTerminal(ticket.Status))
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                $"Ticket {ticket.TicketNumber} is {ticket.Status} and can no longer be edited.");
        }

        // Optimistic concurrency, opted into by the client.
        //
        // If they send back the RowVersion they were shown, we tell EF what they thought the
        // row looked like. If someone else has changed it since, SaveChanges throws and we
        // return 409 instead of silently discarding the other person's edit.
        if (!string.IsNullOrWhiteSpace(dto.RowVersion))
        {
            try
            {
                _uow.Tickets.SetOriginalRowVersion(ticket, Convert.FromBase64String(dto.RowVersion));
            }
            catch (FormatException)
            {
                return ServiceResult<TicketDetailDto>.Invalid("RowVersion is not valid base64.");
            }
        }

        var categoryChanged = ticket.CategoryId != dto.CategoryId;
        if (categoryChanged)
        {
            var routing = await _uow.Categories.GetRoutingInfoAsync(dto.CategoryId, ct);
            if (routing is null)
            {
                return ServiceResult<TicketDetailDto>.Invalid(
                    $"Category {dto.CategoryId} does not exist or is not active.");
            }

            ticket.CategoryId = dto.CategoryId;

            // THE ONE LINE THAT PAYS FOR THE DENORMALISED COLUMN.
            // Ticket.DepartmentId duplicates Category.DepartmentId, and keeping it in sync
            // costs exactly this. In exchange, every security filter in the application is
            // one indexed comparison instead of a join.
            ticket.DepartmentId = routing.Value.DepartmentId;
        }

        var oldPriority = ticket.Priority;

        ticket.Title = dto.Title.Trim();
        ticket.Description = dto.Description.Trim();
        ticket.Priority = dto.Priority;
        ticket.LocationAddress = dto.LocationAddress?.Trim();
        ticket.Latitude = dto.Latitude;
        ticket.Longitude = dto.Longitude;

        var validation = ValidateEntity(ticket);
        if (validation is not null)
        {
            return ServiceResult<TicketDetailDto>.Invalid(validation);
        }

        if (oldPriority != dto.Priority)
        {
            AddHistory(ticket, "Priority", oldPriority.ToString(), dto.Priority.ToString(), null);
        }

        try
        {
            // No repository.Update() call. The entity is tracked, so EF already knows which
            // properties changed and writes only those columns.
            await _uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                "This ticket was changed by someone else while you were editing it. " +
                "Reload it and reapply your changes.");
        }

        await _notifier.SendTicketUpdatedAsync(ticket.Id, new { ticket.Id, ticket.TicketNumber }, ct);

        return await ReloadAsync(ticket.Id, ct);
    }

    // =====================================================================
    // Status
    // =====================================================================

    public async Task<ServiceResult<TicketDetailDto>> ChangeStatusAsync(
        int id, ChangeTicketStatusDto dto, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetForUpdateAsync(id, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.");
        }

        // The workflow table decides, not a chain of ifs scattered through this method.
        if (!TicketWorkflow.CanTransition(ticket.Status, dto.NewStatus))
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                TicketWorkflow.DescribeInvalidTransition(ticket.Status, dto.NewStatus));
        }

        if (TicketWorkflow.RequiresReason(dto.NewStatus) && string.IsNullOrWhiteSpace(dto.Reason))
        {
            return ServiceResult<TicketDetailDto>.Invalid(
                $"Moving a ticket to {dto.NewStatus} requires a reason.");
        }

        var oldStatus = ticket.Status;
        var now = DateTime.UtcNow;

        ticket.Status = dto.NewStatus;

        // Timestamps that go with the state, set together with it so they can never disagree.
        if (dto.NewStatus == TicketStatus.Resolved)
        {
            ticket.ResolvedAt = now;
        }
        else if (dto.NewStatus == TicketStatus.Closed)
        {
            // Closing straight from Resolved keeps the original resolution time. The ??=
            // matters: overwriting it would quietly inflate every resolution-time report.
            ticket.ResolvedAt ??= now;
            ticket.ClosedAt = now;
        }
        else if (dto.NewStatus == TicketStatus.InProgress)
        {
            // Re-opened. Clear the resolution so the ticket does not look finished.
            ticket.ResolvedAt = null;
            ticket.ClosedAt = null;
        }

        AddHistory(ticket, "Status", oldStatus.ToString(), dto.NewStatus.ToString(), dto.Reason);

        await _uow.SaveChangesAsync(ct);

        // Tell the reporter. This is the whole reason a citizen gives us their account.
        if (ticket.CreatedByUserId.HasValue)
        {
            await CreateNotificationAsync(
                ticket.CreatedByUserId.Value,
                NotificationType.TicketStatusChanged,
                $"Ticket {ticket.TicketNumber} is now {dto.NewStatus}",
                string.IsNullOrWhiteSpace(dto.Reason) ? ticket.Title : dto.Reason,
                ticket.Id,
                ct);
        }

        _log.LogInformation("Ticket {TicketNumber} moved {Old} → {New} by user {UserId}",
            ticket.TicketNumber, oldStatus, dto.NewStatus, _currentUser.UserId);

        return await ReloadAsync(ticket.Id, ct);
    }

    /// <summary>
    /// Resolved → InProgress: the reporter says it is not actually fixed.
    /// </summary>
    /// <remarks>
    /// THE ONE STATUS CHANGE A CITIZEN MAY MAKE, which is why it is a separate endpoint rather
    /// than a value posted to the staff-only status endpoint. Loosening that endpoint's
    /// <c>[Authorize(Roles = Staff)]</c> to let one transition through would open all six.
    /// <para/>
    /// Everything after the permission check is delegated to <see cref="ChangeStatusAsync"/> —
    /// which already clears <c>ResolvedAt</c>, writes the history row and notifies the
    /// reporter. The cost is one extra read of the ticket; the gain is that "what happens when
    /// a ticket changes status" is implemented exactly once and cannot drift. Same trade as
    /// <see cref="AutoAssignAsync"/> reusing <see cref="AssignAsync"/>.
    /// </remarks>
    public async Task<ServiceResult<TicketDetailDto>> ReopenAsync(int id, CancellationToken ct = default)
    {
        var found = await GetByIdAsync(id, ct);
        if (!found.IsSuccess || found.Value is null)
        {
            return ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.");
        }

        var ticket = found.Value;

        // Only from Resolved. A Closed ticket is terminal and an Open one was never shut —
        // spelled out here rather than left to the workflow table, because the table would
        // happily allow Open → InProgress and that is not a re-opening, it is picking the
        // ticket up for the first time.
        if (ticket.Status != TicketStatus.Resolved)
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                $"Only a Resolved ticket can be re-opened. {ticket.TicketNumber} is {ticket.Status}.");
        }

        if (!MayTransition(TicketStatus.Resolved, TicketStatus.InProgress, ticket.CreatedByUserId))
        {
            // 403, not 404. The caller can already see this ticket — they got here through the
            // access filter — so there is nothing left to hide, and "you are not allowed" is a
            // far more useful answer than pretending the ticket vanished.
            return ServiceResult<TicketDetailDto>.Forbidden(
                "Only the person who reported this ticket, or a member of staff, can re-open it.");
        }

        return await ChangeStatusAsync(id, new ChangeTicketStatusDto
        {
            NewStatus = TicketStatus.InProgress,
            Reason = "Re-opened: the problem is not resolved."
        }, ct);
    }

    // =====================================================================
    // Assignment
    // =====================================================================

    public async Task<ServiceResult<TicketDetailDto>> AssignAsync(
        int id, AssignTicketDto dto, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetForUpdateAsync(id, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.");
        }

        if (TicketWorkflow.IsTerminal(ticket.Status))
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                $"Ticket {ticket.TicketNumber} is {ticket.Status} and cannot be reassigned.");
        }

        var oldAgentId = ticket.AssignedAgentId;

        if (dto.AgentId is null)
        {
            ticket.AssignedAgentId = null;
            AddHistory(ticket, "AssignedAgent", oldAgentId?.ToString(), null, dto.Note ?? "Unassigned.");
        }
        else
        {
            var agent = await _uow.Agents.GetByIdAsync(dto.AgentId.Value, ct);

            if (agent is null || !agent.IsActive)
            {
                return ServiceResult<TicketDetailDto>.Invalid(
                    $"Agent {dto.AgentId} does not exist or is not active.");
            }

            // The department boundary, enforced on the write path too.
            //
            // Reads are filtered by department, but that is not enough on its own: without
            // this check a supervisor could hand a Roads ticket to a Sanitation agent, and
            // the ticket would then be invisible to everyone who is supposed to work on it.
            if (agent.DepartmentId != ticket.DepartmentId)
            {
                return ServiceResult<TicketDetailDto>.Invalid(
                    "An agent can only be assigned tickets in their own department.");
            }

            var openCount = await _uow.Tickets.CountOpenForAgentAsync(agent.Id, ct);
            if (openCount >= agent.MaxOpenTickets)
            {
                return ServiceResult<TicketDetailDto>.Conflict(
                    $"{agent.FullName} already has {openCount} open tickets " +
                    $"(their limit is {agent.MaxOpenTickets}).");
            }

            ticket.AssignedAgentId = agent.Id;
            AddHistory(ticket, "AssignedAgent", oldAgentId?.ToString(), agent.FullName, dto.Note);

            await CreateNotificationAsync(
                agent.UserId,
                NotificationType.TicketAssigned,
                $"Ticket {ticket.TicketNumber} was assigned to you",
                ticket.Title,
                ticket.Id,
                ct);
        }

        await _uow.SaveChangesAsync(ct);
        return await ReloadAsync(ticket.Id, ct);
    }

    public async Task<ServiceResult<TicketDetailDto>> AutoAssignAsync(int id, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetForUpdateAsync(id, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.");
        }

        var agentId = await _uow.Agents.FindLeastLoadedAgentAsync(ticket.DepartmentId, ct);
        if (agentId is null)
        {
            return ServiceResult<TicketDetailDto>.Conflict(
                "Every agent in this department is at their ticket limit.");
        }

        // Reuse AssignAsync rather than repeating the checks. It reloads the ticket, which
        // is one extra query — and worth it, because there is now exactly one place where
        // "assign a ticket" is implemented, and it cannot drift out of step with itself.
        return await AssignAsync(id, new AssignTicketDto
        {
            AgentId = agentId,
            Note = "Auto-assigned to the least-loaded available agent."
        }, ct);
    }

    // =====================================================================
    // Delete
    // =====================================================================

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetForUpdateAsync(id, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult.NotFound($"Ticket {id} was not found.");
        }

        // Remove() looks like a hard delete and is not: SaveChanges intercepts it, flips the
        // IsDeleted flag, and the global query filter makes the row vanish from every query.
        // The data is still there for the audit trail and for the support request that starts
        // "we deleted it by mistake".
        _uow.Tickets.Remove(ticket);
        await _uow.SaveChangesAsync(ct);

        _log.LogInformation("Ticket {TicketNumber} soft-deleted by user {UserId}",
            ticket.TicketNumber, _currentUser.UserId);

        return ServiceResult.Success();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// Turns the caller's claims into the filter the repository applies to every query.
    /// </summary>
    /// <remarks>
    /// One place decides who sees what. Every read path goes through here, so a change to
    /// the rules is a change to this method — not an audit of forty query call sites.
    /// </remarks>
    private TicketAccessFilter BuildAccessFilter()
    {
        if (_currentUser.IsInRole(AppRoles.Admin))
        {
            return TicketAccessFilter.Everything;
        }

        if ((_currentUser.IsInRole(AppRoles.Supervisor) || _currentUser.IsInRole(AppRoles.Agent))
            && _currentUser.DepartmentId.HasValue)
        {
            return TicketAccessFilter.ForDepartment(_currentUser.DepartmentId.Value);
        }

        if (_currentUser.UserId.HasValue)
        {
            return TicketAccessFilter.ForReporter(_currentUser.UserId.Value);
        }

        // Anonymous, or staff with no department set. Fail closed: an empty list, never
        // the whole table.
        return new TicketAccessFilter(false, null, null);
    }

    private bool IsStaff()
        => _currentUser.IsInRole(AppRoles.Admin)
           || _currentUser.IsInRole(AppRoles.Supervisor)
           || _currentUser.IsInRole(AppRoles.Agent);

    /// <summary>
    /// May the <em>current caller</em> make this particular move? A separate question from
    /// whether the workflow allows it at all.
    /// </summary>
    /// <remarks>
    /// TWO DIFFERENT KINDS OF RULE, AND THEY LIVE IN DIFFERENT PLACES:
    /// <list type="bullet">
    /// <item><see cref="TicketWorkflow"/> knows about <em>states</em> — an OnHold ticket cannot
    ///       jump straight to Closed, no matter who is asking.</item>
    /// <item>This knows about <em>people</em> — driving the workflow is staff work, with one
    ///       exception: the citizen who reported a ticket may push it back from Resolved to
    ///       InProgress. Without that exception "we fixed it" would be the last word, and the
    ///       person standing next to the unfixed pothole would have no way to say otherwise.</item>
    /// </list>
    /// It is one method rather than a check copied into each caller, so
    /// <see cref="GetWorkflowAsync"/> (which tells the client what it may do) and
    /// <see cref="ReopenAsync"/> (which enforces it) can never disagree — the bug where the
    /// button is shown and then the request is refused.
    /// <para/>
    /// Note what this does NOT do: it never widens access. It is only ever consulted for a
    /// ticket the access filter has already let the caller see.
    /// </remarks>
    private bool MayTransition(TicketStatus from, TicketStatus to, int? reportedByUserId)
    {
        if (IsStaff())
        {
            return true;
        }

        return from == TicketStatus.Resolved
               && to == TicketStatus.InProgress
               && reportedByUserId.HasValue
               && reportedByUserId == _currentUser.UserId;
    }

    /// <summary>
    /// Builds the next <c>TKT-2026-000123</c>.
    /// </summary>
    /// <remarks>
    /// HONEST ABOUT THE RACE. Two tickets created in the same millisecond can both read the
    /// same maximum and both propose the same number. The unique index on
    /// <c>TicketNumber</c> means the second INSERT fails rather than producing a duplicate,
    /// and we retry — which is the correct shape: <b>let the database enforce it, and handle
    /// the refusal</b>, rather than pretending a C# check can be atomic.
    /// <para/>
    /// A production system with real volume would use a database sequence instead and skip
    /// the loop entirely. At municipal scale this is fine, and it is a much better teaching
    /// example of what "the check and the insert are not atomic" actually means.
    /// </remarks>
    private async Task<string> GenerateTicketNumberAsync(int year, CancellationToken ct)
    {
        var next = await _uow.Tickets.GetMaxSequenceForYearAsync(year, ct) + 1;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = $"TKT-{year}-{next:D6}";

            if (!await _uow.Tickets.TicketNumberExistsAsync(candidate, ct))
            {
                return candidate;
            }

            next++;
        }

        throw new InvalidOperationException(
            "Could not allocate a unique ticket number after 5 attempts.");
    }

    private void AddHistory(Ticket ticket, string field, string? oldValue, string? newValue, string? note)
        => ticket.History.Add(new TicketHistory
        {
            TicketId = ticket.Id,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            Note = note,
            ChangedAt = DateTime.UtcNow,
            ChangedById = _currentUser.UserId,
            ChangedByName = _currentUser.DisplayName
        });

    /// <summary>
    /// Writes a notification row and pushes it to whoever is connected.
    /// </summary>
    /// <remarks>
    /// ORDER MATTERS: save first, push second. A push that arrives before the row exists
    /// gives the user a notification that disappears when they refresh. The row is the truth.
    /// </remarks>
    private async Task CreateNotificationAsync(
        int userId, NotificationType type, string title, string message, int? ticketId, CancellationToken ct)
    {
        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            TicketId = ticketId,
            Link = ticketId.HasValue ? $"/tickets/{ticketId}" : null,
            CreatedAt = DateTime.UtcNow
        };

        await _uow.Notifications.AddAsync(notification, ct);
        await _uow.SaveChangesAsync(ct);

        await _notifier.SendNotificationAsync(userId, new NotificationDto
        {
            Id = notification.Id,
            Type = notification.Type,
            Title = notification.Title,
            Message = notification.Message,
            Link = notification.Link,
            IsRead = false,
            CreatedAt = notification.CreatedAt
        }, ct);
    }

    private async Task NotifyDepartmentAsync(Ticket ticket, string title, string message, CancellationToken ct)
    {
        var agents = await _uow.Agents.ListAsync(
            a => a.DepartmentId == ticket.DepartmentId && a.IsActive, ct);

        foreach (var agent in agents)
        {
            await CreateNotificationAsync(
                agent.UserId, NotificationType.SlaBreachWarning, title, message, ticket.Id, ct);
        }
    }

    /// <summary>
    /// The extra rules a report with no account behind it has to satisfy. Null when it is fine.
    /// </summary>
    /// <remarks>
    /// Returns a field → messages dictionary rather than one string, so the response is a
    /// <c>ValidationProblemDetails</c> — the identical shape <c>[ApiController]</c> produces
    /// when it rejects a bad model. The form can then highlight the offending box instead of
    /// showing a sentence at the top of the page, and the front end needs one error handler
    /// rather than two.
    /// </remarks>
    private static IDictionary<string, string[]>? ValidateAnonymousReporter(CreateTicketDto dto)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(dto.ReporterName))
        {
            errors[nameof(CreateTicketDto.ReporterName)] = new[]
            {
                "Tell us who is reporting this, so a crew knows who to ask about it."
            };
        }

        // ONE OF THE TWO, not both. Insisting on an email address excludes the people most
        // likely to be phoning about a broken street light; insisting on both just loses
        // reports. One working channel is the actual requirement.
        if (string.IsNullOrWhiteSpace(dto.ReporterEmail) && string.IsNullOrWhiteSpace(dto.ReporterPhone))
        {
            var message = "Give us an email address or a phone number — without one there is " +
                          "no way to tell you what happened to your report.";

            errors[nameof(CreateTicketDto.ReporterEmail)] = new[] { message };
            errors[nameof(CreateTicketDto.ReporterPhone)] = new[] { message };
        }

        return errors.Count == 0 ? null : errors;
    }

    /// <summary>
    /// Runs the entity's own <c>IValidatableObject.Validate</c>.
    /// </summary>
    /// <remarks>
    /// The DTO's attributes already ran during model binding, but they only ever see one
    /// property at a time. "Latitude requires longitude" needs the whole object, and it needs
    /// to hold no matter who built the object — a controller, a seeder, an importer. So it
    /// lives on the entity and we call it here, on the path every writer goes through.
    /// </remarks>
    private static string? ValidateEntity(Ticket ticket)
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(ticket);
        var failure = ticket.Validate(context).FirstOrDefault();
        return failure?.ErrorMessage;
    }

    private async Task<ServiceResult<TicketDetailDto>> ReloadAsync(int id, CancellationToken ct)
    {
        // Re-read through the projection so the caller gets exactly the same shape the GET
        // endpoint returns — including the computed fields and the fresh RowVersion.
        //
        // The alternative, hand-mapping the entity we already have, means two mapping code
        // paths that WILL disagree the first time someone adds a field to only one of them.
        var dto = await _uow.Tickets.GetDetailAsync(id, TicketAccessFilter.Everything, true, ct);

        return dto is null
            ? ServiceResult<TicketDetailDto>.NotFound($"Ticket {id} was not found.")
            : ServiceResult<TicketDetailDto>.Success(dto);
    }
}
