using Microsoft.Extensions.Options;
using TicketHub.BusinessLogic.Options;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Attachments;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Notifications;
using TicketHub.Contracts.Ratings;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

// =========================================================================
// Who may see which ticket
// =========================================================================

/// <summary>
/// Turns the caller's claims into the filter every ticket query is run through.
/// </summary>
/// <remarks>
/// The rule itself is explained on <c>TicketService.BuildAccessFilter</c>, and it is the same
/// rule — deliberately, because a system where "may I see this ticket?" has two answers
/// depending on which endpoint you came in through is a system with a hole in it.
/// <para/>
/// The services below reach a ticket sideways: through its attachments, through its rating.
/// A child route is not a way around the parent's access rule, so they ask the same question
/// here rather than each writing their own version of it.
/// </remarks>
internal static class TicketAccess
{
    public static TicketAccessFilter Of(ICurrentUser user)
    {
        if (user.IsInRole(AppRoles.Admin))
        {
            return TicketAccessFilter.Everything;
        }

        if ((user.IsInRole(AppRoles.Supervisor) || user.IsInRole(AppRoles.Agent))
            && user.DepartmentId.HasValue)
        {
            return TicketAccessFilter.ForDepartment(user.DepartmentId.Value);
        }

        if (user.UserId.HasValue)
        {
            return TicketAccessFilter.ForReporter(user.UserId.Value);
        }

        // Anonymous, or staff with no department. Fail closed — an empty result, never the
        // whole table.
        return new TicketAccessFilter(false, null, null);
    }
}

// =========================================================================
// Notifications
// =========================================================================

public interface INotificationService
{
    Task<PagedResult<NotificationDto>> GetMineAsync(NotificationQuery query, CancellationToken ct = default);
    Task<UnreadCountDto> GetUnreadCountAsync(CancellationToken ct = default);
    Task<ServiceResult> MarkReadAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> MarkAllReadAsync(CancellationToken ct = default);
}

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;

    public NotificationService(IUnitOfWork uow, ICurrentUser currentUser)
    {
        _uow = uow;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<NotificationDto>> GetMineAsync(
        NotificationQuery query, CancellationToken ct = default)
    {
        // There is no "get notifications for user X" endpoint anywhere in this API, and that
        // is not an oversight. The user id comes from the token, so the question "whose
        // notifications?" has exactly one possible answer and cannot be tampered with.
        if (_currentUser.UserId is null)
        {
            return PagedResult<NotificationDto>.Empty(query.Page, query.PageSize);
        }

        return await _uow.Notifications.GetForUserAsync(_currentUser.UserId.Value, query, ct);
    }

    public async Task<UnreadCountDto> GetUnreadCountAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return new UnreadCountDto { Count = 0 };
        }

        return new UnreadCountDto
        {
            Count = await _uow.Notifications.GetUnreadCountAsync(_currentUser.UserId.Value, ct)
        };
    }

    public async Task<ServiceResult> MarkReadAsync(int id, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult.Forbidden();
        }

        // The user id is part of the lookup, not checked afterwards. Someone else's
        // notification is simply not found — no window in which the wrong row is loaded.
        var notification = await _uow.Notifications.GetForUpdateAsync(id, _currentUser.UserId.Value, ct);
        if (notification is null)
        {
            return ServiceResult.NotFound("Notification not found.");
        }

        if (notification.IsRead)
        {
            return ServiceResult.Success();   // idempotent: reading twice is not an error
        }

        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync(ct);
        return ServiceResult.Success();
    }

    public async Task<ServiceResult> MarkAllReadAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult.Forbidden();
        }

        await _uow.Notifications.MarkAllReadAsync(_currentUser.UserId.Value, ct);
        return ServiceResult.Success();
    }
}

// =========================================================================
// Ratings
// =========================================================================

public interface IRatingService
{
    /// <summary>The ticket's rating. NotFound while it is still unrated.</summary>
    Task<ServiceResult<RatingDto>> GetAsync(int ticketId, CancellationToken ct = default);

    Task<ServiceResult<RatingDto>> RateAsync(int ticketId, CreateRatingDto dto, CancellationToken ct = default);
    Task<PagedResult<CategorySatisfactionDto>> GetCategorySatisfactionAsync(PagedQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<DailyTicketCountDto>> GetDailyVolumeAsync(DateTime from, DateTime to, int? departmentId, CancellationToken ct = default);
}

public class RatingService : IRatingService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;

    public RatingService(IUnitOfWork uow, ICurrentUser currentUser)
    {
        _uow = uow;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The score this ticket was given, if it has been given one.
    /// </summary>
    /// <remarks>
    /// TWO DIFFERENT "NOT FOUND"S, AND THEY MEAN DIFFERENT THINGS TO A CLIENT.
    /// The first is "there is no such ticket, or it is not yours" — the deliberate ambiguity
    /// explained in <c>TicketService.GetByIdAsync</c>. The second is "the ticket is real and
    /// you can see it, but nobody has rated it yet", which is the normal state of most
    /// tickets and is what tells a front end to show the rating form.
    /// <para/>
    /// The rating comes off the ticket's own projection rather than from a second query
    /// against Ratings. That reads more rows than a targeted SELECT would — but it means the
    /// access rule and the mapping are the ones already written and tested for the detail
    /// screen, instead of a second copy of each that can quietly disagree with the first.
    /// </remarks>
    public async Task<ServiceResult<RatingDto>> GetAsync(int ticketId, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetDetailAsync(
            ticketId,
            TicketAccess.Of(_currentUser),

            // Not a permission decision — nothing here reads the comments. It only chooses
            // which sub-select runs, and false is the cheaper one.
            includeInternalComments: false,
            ct);

        if (ticket is null)
        {
            return ServiceResult<RatingDto>.NotFound($"Ticket {ticketId} was not found.");
        }

        return ticket.Rating is null
            ? ServiceResult<RatingDto>.NotFound($"Ticket {ticket.TicketNumber} has not been rated yet.")
            : ServiceResult<RatingDto>.Success(ticket.Rating);
    }

    public async Task<ServiceResult<RatingDto>> RateAsync(
        int ticketId, CreateRatingDto dto, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetByIdAsync(ticketId, ct);
        if (ticket is null)
        {
            return ServiceResult<RatingDto>.NotFound($"Ticket {ticketId} was not found.");
        }

        // Only the person who reported it may rate it. Not the agent who fixed it, and not
        // a passing admin — a satisfaction score is only worth anything if it comes from the
        // person who was actually dissatisfied.
        if (ticket.CreatedByUserId != _currentUser.UserId)
        {
            return ServiceResult<RatingDto>.Forbidden("Only the person who reported a ticket can rate it.");
        }

        if (ticket.Status is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            return ServiceResult<RatingDto>.Conflict("You can only rate a ticket once it has been resolved.");
        }

        if (await _uow.Repository<Rating>().ExistsAsync(r => r.TicketId == ticketId, ct))
        {
            // The friendly half. The unique index on TicketId is the half that is true even
            // when two requests arrive at the same instant.
            return ServiceResult<RatingDto>.Conflict("This ticket has already been rated.");
        }

        var rating = new Rating
        {
            TicketId = ticketId,
            Stars = dto.Stars,
            Comment = dto.Comment?.Trim(),
            RatedByUserId = _currentUser.UserId
        };

        await _uow.Repository<Rating>().AddAsync(rating, ct);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult<RatingDto>.Success(new RatingDto
        {
            Id = rating.Id,
            TicketId = ticketId,
            Stars = rating.Stars,
            Comment = rating.Comment,
            CreatedAt = rating.CreatedAt,
            RatedByName = _currentUser.DisplayName
        });
    }

    public Task<PagedResult<CategorySatisfactionDto>> GetCategorySatisfactionAsync(
        PagedQuery query, CancellationToken ct = default)
        => _uow.Reports.GetCategorySatisfactionAsync(query, ct);

    public Task<IReadOnlyList<DailyTicketCountDto>> GetDailyVolumeAsync(
        DateTime from, DateTime to, int? departmentId, CancellationToken ct = default)
        => _uow.Reports.GetDailyVolumeAsync(from, to, departmentId, ct);
}

// =========================================================================
// Attachments
// =========================================================================

/// <summary>What the API hands the service after it has read the upload.</summary>
/// <remarks>
/// A plain record, not <c>IFormFile</c>. If this signature mentioned <c>IFormFile</c> the
/// business layer would need a reference to ASP.NET Core, and a unit test would have to mock
/// a web type to check a file-size rule.
/// </remarks>
public record UploadedFile(string FileName, string ContentType, long SizeBytes, Stream Content);

public interface IAttachmentService
{
    /// <summary>The ticket's files — metadata only, never the bytes.</summary>
    Task<ServiceResult<IReadOnlyList<AttachmentDto>>> ListAsync(int ticketId, CancellationToken ct = default);

    Task<ServiceResult<AttachmentDto>> UploadAsync(int ticketId, UploadedFile file, string? description, CancellationToken ct = default);
    Task<ServiceResult<(Stream Content, string ContentType, string FileName)>> DownloadAsync(int ticketId, int attachmentId, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int ticketId, int attachmentId, CancellationToken ct = default);
}

public class AttachmentService : IAttachmentService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly FileStorageOptions _options;
    private readonly string _rootPath;

    public AttachmentService(
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IOptions<FileStorageOptions> options)
    {
        _uow = uow;
        _currentUser = currentUser;
        _options = options.Value;

        _rootPath = Path.IsPathRooted(_options.RootPath)
            ? _options.RootPath
            : Path.Combine(AppContext.BaseDirectory, _options.RootPath);

        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// What is attached to this ticket, without transferring any of it.
    /// </summary>
    /// <remarks>
    /// METADATA ONLY, AND THAT IS THE WHOLE POINT. Each row carries a <c>DownloadUrl</c>, so a
    /// gallery can render twelve thumbnails' worth of file names and sizes from one small JSON
    /// response and fetch the bytes only for what the user actually opens. An endpoint that
    /// returned the files themselves would make "list the attachments" cost forty megabytes.
    /// <para/>
    /// Access-filtered through the ticket, like every other way into a ticket's children — see
    /// <see cref="TicketAccess"/>. Note this is a stricter rule than
    /// <see cref="DownloadAsync"/> currently applies, which is worth knowing about rather than
    /// assuming; that method checks the attachment belongs to the ticket in the route, but not
    /// that the caller may see the ticket.
    /// </remarks>
    public async Task<ServiceResult<IReadOnlyList<AttachmentDto>>> ListAsync(
        int ticketId, CancellationToken ct = default)
    {
        var ticket = await _uow.Tickets.GetDetailAsync(
            ticketId,
            TicketAccess.Of(_currentUser),
            includeInternalComments: false,   // nothing here reads the comments
            ct);

        return ticket is null
            ? ServiceResult<IReadOnlyList<AttachmentDto>>.NotFound($"Ticket {ticketId} was not found.")
            : ServiceResult<IReadOnlyList<AttachmentDto>>.Success(ticket.Attachments);
    }

    public async Task<ServiceResult<AttachmentDto>> UploadAsync(
        int ticketId, UploadedFile file, string? description, CancellationToken ct = default)
    {
        // Access-filtered, not a bare id lookup. Uploading is a WRITE to somebody else's
        // ticket: without the filter any signed-in user can attach a file to any ticket in
        // the system, which is both a data-integrity problem and a very cheap way to plant
        // content on a case you are not part of.
        var ticket = await _uow.Tickets.GetForUpdateAsync(ticketId, TicketAccess.Of(_currentUser), ct);
        if (ticket is null)
        {
            return ServiceResult<AttachmentDto>.NotFound($"Ticket {ticketId} was not found.");
        }

        if (file.SizeBytes <= 0)
        {
            return ServiceResult<AttachmentDto>.Invalid("The file is empty.");
        }

        if (file.SizeBytes > _options.MaxFileSizeBytes)
        {
            return ServiceResult<AttachmentDto>.Invalid(
                $"Files must be {_options.MaxFileSizeBytes / 1024 / 1024} MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        // ALLOW list, not block list. A block list is always missing the one extension that
        // matters; an allow list only has to know what we support.
        if (!_options.AllowedExtensions.Contains(extension))
        {
            return ServiceResult<AttachmentDto>.Invalid(
                $"'{extension}' files are not accepted. Allowed: {string.Join(", ", _options.AllowedExtensions)}");
        }

        // ⚠ THE STORED NAME IS OURS, NOT THEIRS.
        //
        // Two independent reasons, both real:
        //   1. Two people uploading "photo.jpg" must not overwrite each other.
        //   2. A filename that arrived from the internet can contain "../../" and walk
        //      straight out of the upload folder into somewhere far more interesting.
        //      This is path traversal, and it is a whole class of bug that simply does not
        //      exist if you never build a path from user input.
        //
        // We keep their name in the FileName column for display, and it is never used to
        // touch the filesystem.
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var relativePath = Path.Combine(ticketId.ToString(), storedName);
        var absolutePath = Path.Combine(_rootPath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using (var target = File.Create(absolutePath))
        {
            await file.Content.CopyToAsync(target, ct);
        }

        var attachment = new TicketAttachment
        {
            TicketId = ticketId,
            FileName = Path.GetFileName(file.FileName),   // strip any directory part they sent
            StoragePath = relativePath,
            ContentType = file.ContentType,
            SizeBytes = file.SizeBytes,
            Description = description?.Trim(),
            UploadedById = _currentUser.UserId
        };

        await _uow.Repository<TicketAttachment>().AddAsync(attachment, ct);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult<AttachmentDto>.Success(new AttachmentDto
        {
            Id = attachment.Id,
            TicketId = ticketId,
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            SizeBytes = attachment.SizeBytes,
            DownloadUrl = $"/api/tickets/{ticketId}/attachments/{attachment.Id}",
            UploadedById = attachment.UploadedById,
            UploadedByName = _currentUser.DisplayName,
            CreatedAt = attachment.CreatedAt
        });
    }

    public async Task<ServiceResult<(Stream Content, string ContentType, string FileName)>> DownloadAsync(
        int ticketId, int attachmentId, CancellationToken ct = default)
    {
        // THREE checks, and all three are load-bearing.
        //
        // 1. Can this caller see the PARENT TICKET at all? This is the one that is easy to
        //    forget, because the route already contains a ticket id and it is tempting to
        //    treat that as proof. It is not: the id in the URL is supplied by the caller.
        //    Without this, any signed-in user downloads any ticket's evidence by pairing an
        //    attachment id with its own correct ticket id — both of which are just small
        //    integers. The list endpoint 404s for them while the file itself hands over.
        // 2. Does the attachment exist?
        // 3. Does it belong to THAT ticket? Without this, a caller pairs someone else's
        //    attachment id with a ticket they legitimately own and the route looks perfect.
        //
        // Same access filter as everywhere else, so "who may see this ticket" is decided in
        // exactly one place and cannot drift between endpoints.
        var ticket = await _uow.Tickets.GetForUpdateAsync(ticketId, TicketAccess.Of(_currentUser), ct);
        if (ticket is null)
        {
            // 404, not 403 — replying 403 would confirm the ticket exists. Same reasoning as
            // TicketService.GetByIdAsync.
            return ServiceResult<(Stream, string, string)>.NotFound("Attachment not found.");
        }

        var attachment = await _uow.Repository<TicketAttachment>().GetByIdAsync(attachmentId, ct);
        if (attachment is null || attachment.TicketId != ticketId)
        {
            return ServiceResult<(Stream, string, string)>.NotFound("Attachment not found.");
        }

        var absolutePath = Path.Combine(_rootPath, attachment.StoragePath);
        if (!File.Exists(absolutePath))
        {
            return ServiceResult<(Stream, string, string)>.NotFound(
                "The file is recorded but missing from storage.");
        }

        Stream stream = File.OpenRead(absolutePath);
        return ServiceResult<(Stream, string, string)>.Success(
            (stream, attachment.ContentType, attachment.FileName));
    }

    public async Task<ServiceResult> DeleteAsync(int ticketId, int attachmentId, CancellationToken ct = default)
    {
        // The parent-ticket check first, for the same reason as DownloadAsync — otherwise
        // staff in one department can delete evidence from another department's tickets.
        var ticket = await _uow.Tickets.GetForUpdateAsync(ticketId, TicketAccess.Of(_currentUser), ct);
        if (ticket is null)
        {
            return ServiceResult.NotFound("Attachment not found.");
        }

        var attachment = await _uow.Repository<TicketAttachment>().GetByIdAsync(attachmentId, ct);
        if (attachment is null || attachment.TicketId != ticketId)
        {
            return ServiceResult.NotFound("Attachment not found.");
        }

        // Soft delete for the row; the bytes stay on disk.
        //
        // That is deliberate, and it is the honest trade: if the row can be restored, the
        // file it points at had better still be there. A separate cleanup job removes files
        // whose row has been soft-deleted for more than N days. Deleting the file here would
        // make "restore" a lie.
        _uow.Repository<TicketAttachment>().Remove(attachment);
        await _uow.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }
}
