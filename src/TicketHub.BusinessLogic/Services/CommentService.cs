using Microsoft.Extensions.Logging;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Comments;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

public interface ICommentService
{
    Task<ServiceResult<PagedResult<CommentDto>>> GetForTicketAsync(
        int ticketId, CommentQuery query, CancellationToken ct = default);

    Task<ServiceResult<CommentDto>> AddAsync(int ticketId, CreateCommentDto dto, CancellationToken ct = default);
    Task<ServiceResult<CommentDto>> UpdateAsync(int commentId, UpdateCommentDto dto, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int commentId, CancellationToken ct = default);
}

/// <summary>
/// Comments on tickets. Short, but it is where two authorization rules live that are easy
/// to get wrong.
/// </summary>
public class CommentService : ICommentService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<CommentService> _log;

    public CommentService(
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IRealtimeNotifier notifier,
        ILogger<CommentService> log)
    {
        _uow = uow;
        _currentUser = currentUser;
        _notifier = notifier;
        _log = log;
    }

    public async Task<ServiceResult<PagedResult<CommentDto>>> GetForTicketAsync(
        int ticketId, CommentQuery query, CancellationToken ct = default)
    {
        // Check the TICKET is visible before returning its comments.
        //
        // Without this, /api/tickets/999/comments happily returns the comments of a ticket
        // the caller may not read. Access control on the parent has to be checked when you
        // enter through a child route — the route is not the boundary, the data is.
        var ticket = await _uow.Tickets.GetDetailAsync(ticketId, BuildAccessFilter(), IsStaff(), ct);
        if (ticket is null)
        {
            return ServiceResult<PagedResult<CommentDto>>.NotFound($"Ticket {ticketId} was not found.");
        }

        // A citizen never sees internal notes, whatever the query string says.
        // Overriding the caller's own parameter is deliberate: the request is an input, not
        // an instruction, and a security decision is never the caller's to make.
        if (!IsStaff())
        {
            query.IncludeInternal = false;
        }

        var comments = await _uow.Comments.GetForTicketAsync(ticketId, query, ct);
        return ServiceResult<PagedResult<CommentDto>>.Success(comments);
    }

    public async Task<ServiceResult<CommentDto>> AddAsync(
        int ticketId, CreateCommentDto dto, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetForUpdateAsync(ticketId, BuildAccessFilter(), ct);
        if (ticket is null)
        {
            return ServiceResult<CommentDto>.NotFound($"Ticket {ticketId} was not found.");
        }

        if (ticket.Status == TicketStatus.Cancelled)
        {
            return ServiceResult<CommentDto>.Conflict("You cannot comment on a cancelled ticket.");
        }

        var comment = new TicketComment
        {
            TicketId = ticketId,
            Body = dto.Body.Trim(),

            // Same rule as everywhere: forced false for non-staff. A citizen marking their
            // comment internal would hide it from exactly the people meant to read it.
            IsInternal = dto.IsInternal && IsStaff(),

            // From the token, not the body.
            AuthorId = _currentUser.UserId,
            AuthorNameSnapshot = _currentUser.DisplayName ?? _currentUser.Email ?? "Unknown"
        };

        await _uow.Comments.AddAsync(comment, ct);
        await _uow.SaveChangesAsync(ct);

        // Tell the other side. A public comment from staff goes to the reporter; a comment
        // from the reporter goes to the assigned agent. Internal notes notify nobody outside.
        if (!comment.IsInternal)
        {
            await NotifyCounterpartAsync(ticket, comment, ct);
        }

        await _notifier.SendTicketUpdatedAsync(ticketId, new { ticketId, commentId = comment.Id }, ct);

        var dtoResult = await _uow.Comments.GetDtoAsync(comment.Id, ct);
        return dtoResult is null
            ? ServiceResult<CommentDto>.NotFound()
            : ServiceResult<CommentDto>.Success(dtoResult);
    }

    public async Task<ServiceResult<CommentDto>> UpdateAsync(
        int commentId, UpdateCommentDto dto, CancellationToken ct = default)
    {
        var comment = await _uow.Comments.GetForUpdateAsync(commentId, ct);
        if (comment is null)
        {
            return ServiceResult<CommentDto>.NotFound($"Comment {commentId} was not found.");
        }

        // OWNERSHIP — a rule a role cannot express.
        //
        // [Authorize(Roles = "Agent")] answers "may you edit comments". It cannot answer
        // "may you edit THIS comment", because that depends on the row. This is the point
        // where roles stop being enough and you check the resource itself.
        if (!CanModify(comment))
        {
            return ServiceResult<CommentDto>.Forbidden("You can only edit your own comments.");
        }

        comment.Body = dto.Body.Trim();

        // UpdatedAt is stamped by SaveChanges, and the DTO turns it into WasEdited. The
        // timeline stays honest without anybody remembering to set a flag.
        await _uow.SaveChangesAsync(ct);

        var updated = await _uow.Comments.GetDtoAsync(commentId, ct);
        return updated is null
            ? ServiceResult<CommentDto>.NotFound()
            : ServiceResult<CommentDto>.Success(updated);
    }

    public async Task<ServiceResult> DeleteAsync(int commentId, CancellationToken ct = default)
    {
        var comment = await _uow.Comments.GetForUpdateAsync(commentId, ct);
        if (comment is null)
        {
            return ServiceResult.NotFound($"Comment {commentId} was not found.");
        }

        if (!CanModify(comment))
        {
            return ServiceResult.Forbidden("You can only delete your own comments.");
        }

        _uow.Comments.Remove(comment);   // soft delete — see SaveChanges
        await _uow.SaveChangesAsync(ct);

        _log.LogInformation("Comment {CommentId} deleted by user {UserId}", commentId, _currentUser.UserId);

        return ServiceResult.Success();
    }

    // ---------------------------------------------------------------------

    private bool CanModify(TicketComment comment)
        => _currentUser.IsInRole(AppRoles.Admin)
           || (comment.AuthorId.HasValue && comment.AuthorId == _currentUser.UserId);

    private bool IsStaff()
        => _currentUser.IsInRole(AppRoles.Admin)
           || _currentUser.IsInRole(AppRoles.Supervisor)
           || _currentUser.IsInRole(AppRoles.Agent);

    private TicketAccessFilter BuildAccessFilter()
    {
        if (_currentUser.IsInRole(AppRoles.Admin))
        {
            return TicketAccessFilter.Everything;
        }

        if (IsStaff() && _currentUser.DepartmentId.HasValue)
        {
            return TicketAccessFilter.ForDepartment(_currentUser.DepartmentId.Value);
        }

        return _currentUser.UserId.HasValue
            ? TicketAccessFilter.ForReporter(_currentUser.UserId.Value)
            : new TicketAccessFilter(false, null, null);
    }

    private async Task NotifyCounterpartAsync(Ticket ticket, TicketComment comment, CancellationToken ct)
    {
        // Work out who did NOT write this, and tell them.
        int? recipient = null;

        if (IsStaff() && ticket.CreatedByUserId.HasValue)
        {
            recipient = ticket.CreatedByUserId;
        }
        else if (ticket.AssignedAgentId.HasValue)
        {
            var agent = await _uow.Agents.GetByIdAsync(ticket.AssignedAgentId.Value, ct);
            recipient = agent?.UserId;
        }

        // Never notify someone about their own comment.
        if (recipient is null || recipient == _currentUser.UserId)
        {
            return;
        }

        var notification = new Notification
        {
            UserId = recipient.Value,
            Type = NotificationType.CommentAdded,
            Title = $"New comment on {ticket.TicketNumber}",
            Message = comment.Body.Length > 120 ? comment.Body[..120] + "…" : comment.Body,
            TicketId = ticket.Id,
            Link = $"/tickets/{ticket.Id}",
            CreatedAt = DateTime.UtcNow
        };

        await _uow.Notifications.AddAsync(notification, ct);
        await _uow.SaveChangesAsync(ct);

        await _notifier.SendNotificationAsync(recipient.Value, new Contracts.Notifications.NotificationDto
        {
            Id = notification.Id,
            Type = notification.Type,
            Title = notification.Title,
            Message = notification.Message,
            Link = notification.Link,
            CreatedAt = notification.CreatedAt
        }, ct);
    }
}
