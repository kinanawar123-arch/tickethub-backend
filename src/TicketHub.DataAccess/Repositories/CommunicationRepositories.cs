using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TicketHub.Contracts.Chat;
using TicketHub.Contracts.Comments;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.Contracts.Notifications;
using TicketHub.Contracts.Ratings;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Repositories;

// =========================================================================
// Comments
// =========================================================================

public interface ICommentRepository : IRepository<TicketComment>
{
    Task<PagedResult<CommentDto>> GetForTicketAsync(
        int ticketId, CommentQuery query, CancellationToken ct = default);

    Task<CommentDto?> GetDtoAsync(int id, CancellationToken ct = default);

    /// <summary>Tracked, for edit and delete.</summary>
    Task<TicketComment?> GetForUpdateAsync(int id, CancellationToken ct = default);
}

public class CommentRepository : Repository<TicketComment>, ICommentRepository
{
    public CommentRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<CommentDto>> GetForTicketAsync(
        int ticketId, CommentQuery query, CancellationToken ct = default)
    {
        var comments = Db.TicketComments.AsNoTracking().Where(c => c.TicketId == ticketId);

        // Internal notes are removed HERE, in SQL — not in the controller and certainly not
        // in the front end. If a caller must not see a row, that row must never be in the
        // response body.
        if (!query.IncludeInternal)
        {
            comments = comments.Where(c => !c.IsInternal);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            comments = comments.Where(c => EF.Functions.Like(c.Body, $"%{term}%"));
        }

        var total = await comments.CountAsync(ct);

        var items = await comments
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<CommentDto>(items, total, query.Page, query.PageSize);
    }

    public Task<CommentDto?> GetDtoAsync(int id, CancellationToken ct = default)
        => Db.TicketComments.AsNoTracking()
             .Where(c => c.Id == id)
             .Select(Projection)
             .FirstOrDefaultAsync(ct)!;

    public Task<TicketComment?> GetForUpdateAsync(int id, CancellationToken ct = default)
        => Db.TicketComments.FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Reusable projection. See the long note on DepartmentRepository.Projection.</summary>
    private static readonly Expression<Func<TicketComment, CommentDto>> Projection = c => new CommentDto
    {
        Id = c.Id,
        TicketId = c.TicketId,
        Body = c.Body,
        IsInternal = c.IsInternal,
        AuthorId = c.AuthorId,
        AuthorName = c.AuthorNameSnapshot,
        IsFromAgent = c.Ticket.AssignedAgent != null && c.AuthorId == c.Ticket.AssignedAgent.UserId,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };
}

// =========================================================================
// Notifications
// =========================================================================

public interface INotificationRepository : IRepository<Notification>
{
    Task<PagedResult<NotificationDto>> GetForUserAsync(
        int userId, NotificationQuery query, CancellationToken ct = default);

    Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default);

    /// <summary>Marks everything unread as read, in one UPDATE.</summary>
    Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default);

    Task<Notification?> GetForUpdateAsync(int id, int userId, CancellationToken ct = default);
}

public class NotificationRepository : Repository<Notification>, INotificationRepository
{
    public NotificationRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<NotificationDto>> GetForUserAsync(
        int userId, NotificationQuery query, CancellationToken ct = default)
    {
        var notifications = Db.Notifications.AsNoTracking().Where(n => n.UserId == userId);

        if (query.IsRead.HasValue)
        {
            notifications = notifications.Where(n => n.IsRead == query.IsRead.Value);
        }

        if (query.Type.HasValue)
        {
            notifications = notifications.Where(n => n.Type == query.Type.Value);
        }

        var total = await notifications.CountAsync(ct);

        var items = await notifications
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                Link = n.Link,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
                ReadAt = n.ReadAt
            })
            .ToListAsync(ct);

        return new PagedResult<NotificationDto>(items, total, query.Page, query.PageSize);
    }

    public Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default)
        => Db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    public Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default)
        // ExecuteUpdateAsync issues ONE UPDATE statement.
        //
        // The alternative — load every unread notification, set IsRead on each, SaveChanges —
        // reads every row into memory and sends one UPDATE per row. For a user with 400
        // unread items that is 401 round trips instead of 1.
        //
        // The catch worth knowing: this bypasses the change tracker entirely, so it does NOT
        // go through our SaveChanges auditing. That is acceptable here (Notification is not
        // an AuditableEntity and marking one read is not interesting history) — but do not
        // reach for it on a table where the audit trail matters.
        => Db.Notifications
             .Where(n => n.UserId == userId && !n.IsRead)
             .ExecuteUpdateAsync(
                 s => s.SetProperty(n => n.IsRead, true)
                       .SetProperty(n => n.ReadAt, DateTime.UtcNow),
                 ct);

    public Task<Notification?> GetForUpdateAsync(int id, int userId, CancellationToken ct = default)
        // The userId is part of the WHERE, not checked afterwards in C#. That way there is
        // no window in which the wrong row is even loaded — and no chance of forgetting the
        // check at one of the call sites.
        => Db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);
}

// =========================================================================
// Chat
// =========================================================================

public interface IChatRepository : IRepository<Conversation>
{
    Task<PagedResult<ConversationDto>> GetConversationsForUserAsync(
        int userId, ConversationQuery query, CancellationToken ct = default);

    Task<ConversationDto?> GetConversationAsync(int conversationId, int userId, CancellationToken ct = default);

    /// <summary>
    /// The authorization check every hub method must make first.
    /// </summary>
    /// <remarks>
    /// A SignalR hub method is as publicly reachable as a controller action. Anyone who can
    /// open a WebSocket can call <c>SendMessage(conversationId: 999, ...)</c> with any number
    /// they like. Membership is what makes that a 403 instead of a leak.
    /// </remarks>
    Task<bool> IsParticipantAsync(int conversationId, int userId, CancellationToken ct = default);

    Task<IReadOnlyList<int>> GetParticipantUserIdsAsync(int conversationId, CancellationToken ct = default);

    Task<PagedResult<ChatMessageDto>> GetMessagesAsync(
        int conversationId, MessageQuery query, CancellationToken ct = default);

    Task<Conversation?> GetForTicketAsync(int ticketId, CancellationToken ct = default);

    Task<ChatMessageDto?> GetMessageDtoAsync(int messageId, CancellationToken ct = default);

    Task MarkReadAsync(int conversationId, int userId, CancellationToken ct = default);

    /// <summary>Has this exact client message already been stored? Makes a retried send idempotent.</summary>
    Task<ChatMessage?> FindByClientMessageIdAsync(
        int conversationId, string clientMessageId, CancellationToken ct = default);
}

public class ChatRepository : Repository<Conversation>, IChatRepository
{
    public ChatRepository(TicketHubDbContext db) : base(db) { }

    public async Task<PagedResult<ConversationDto>> GetConversationsForUserAsync(
        int userId, ConversationQuery query, CancellationToken ct = default)
    {
        // Start from Conversations and filter by membership with Any(), rather than starting
        // from ConversationParticipants and navigating out. Same rows, but this shape keeps
        // the projection below straightforward.
        var conversations = Db.Conversations.AsNoTracking()
            .Where(c => c.Participants.Any(p => p.UserId == userId));

        if (query.Type.HasValue)
        {
            conversations = conversations.Where(c => c.Type == query.Type.Value);
        }

        if (query.TicketId.HasValue)
        {
            conversations = conversations.Where(c => c.TicketId == query.TicketId.Value);
        }

        if (query.UnreadOnly == true)
        {
            conversations = conversations.Where(c =>
                c.Messages.Any(m =>
                    m.SenderId != userId &&
                    c.Participants.Any(p => p.UserId == userId &&
                                            (p.LastReadAt == null || m.SentAt > p.LastReadAt))));
        }

        var total = await conversations.CountAsync(ct);

        var items = await conversations
            // Ordering by the denormalised LastMessageAt. Without that column this would be
            // a correlated MAX(SentAt) per row — the classic slow inbox.
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(c => new ConversationDto
            {
                Id = c.Id,
                Type = c.Type,
                TicketId = c.TicketId,
                TicketNumber = c.Ticket != null ? c.Ticket.TicketNumber : null,
                Title = c.Title,
                CreatedAt = c.CreatedAt,

                Participants = c.Participants.Select(p => new ConversationParticipantDto
                {
                    UserId = p.UserId,
                    DisplayName = p.User.DisplayName,
                    LastReadAt = p.LastReadAt
                }).ToList(),

                LastMessage = c.Messages
                    .OrderByDescending(m => m.Id)
                    .Select(m => new ChatMessageDto
                    {
                        Id = m.Id,
                        ConversationId = m.ConversationId,
                        Body = m.Body,
                        SenderId = m.SenderId ?? 0,
                        SenderName = m.SenderNameSnapshot,
                        SentAt = m.SentAt,
                        IsSystemMessage = m.IsSystemMessage
                    })
                    .FirstOrDefault(),

                UnreadCount = c.Messages.Count(m =>
                    m.SenderId != userId &&
                    c.Participants.Any(p => p.UserId == userId &&
                                            (p.LastReadAt == null || m.SentAt > p.LastReadAt)))
            })
            .ToListAsync(ct);

        return new PagedResult<ConversationDto>(items, total, query.Page, query.PageSize);
    }

    public Task<ConversationDto?> GetConversationAsync(
        int conversationId, int userId, CancellationToken ct = default)
        => Db.Conversations.AsNoTracking()
             .Where(c => c.Id == conversationId && c.Participants.Any(p => p.UserId == userId))
             .Select(c => new ConversationDto
             {
                 Id = c.Id,
                 Type = c.Type,
                 TicketId = c.TicketId,
                 TicketNumber = c.Ticket != null ? c.Ticket.TicketNumber : null,
                 Title = c.Title,
                 CreatedAt = c.CreatedAt,
                 Participants = c.Participants.Select(p => new ConversationParticipantDto
                 {
                     UserId = p.UserId,
                     DisplayName = p.User.DisplayName,
                     LastReadAt = p.LastReadAt
                 }).ToList(),
                 UnreadCount = c.Messages.Count(m =>
                     m.SenderId != userId &&
                     c.Participants.Any(p => p.UserId == userId &&
                                             (p.LastReadAt == null || m.SentAt > p.LastReadAt)))
             })
             .FirstOrDefaultAsync(ct)!;

    public Task<bool> IsParticipantAsync(int conversationId, int userId, CancellationToken ct = default)
        => Db.ConversationParticipants.AsNoTracking()
             .AnyAsync(p => p.ConversationId == conversationId && p.UserId == userId, ct);

    public async Task<IReadOnlyList<int>> GetParticipantUserIdsAsync(
        int conversationId, CancellationToken ct = default)
        => await Db.ConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversationId)
            .Select(p => p.UserId)
            .ToListAsync(ct);

    public async Task<PagedResult<ChatMessageDto>> GetMessagesAsync(
        int conversationId, MessageQuery query, CancellationToken ct = default)
    {
        var messages = Db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

        // CURSOR PAGING, not Skip/Take.
        //
        // In a chat, new messages arrive at one end constantly. With Skip/Take, three new
        // messages between "page 1" and "page 2" shift everything by three, so you see three
        // messages twice and miss none — or, scrolling the other way, miss three entirely.
        // "Everything older than id N" is stable no matter what arrives meanwhile.
        if (query.BeforeMessageId.HasValue)
        {
            messages = messages.Where(m => m.Id < query.BeforeMessageId.Value);
        }

        var total = await messages.CountAsync(ct);

        var items = await messages
            .OrderByDescending(m => m.Id)
            .Take(query.PageSize)
            .Select(m => new ChatMessageDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                Body = m.Body,
                SenderId = m.SenderId ?? 0,
                SenderName = m.SenderNameSnapshot,
                SentAt = m.SentAt,
                IsSystemMessage = m.IsSystemMessage,
                ClientMessageId = m.ClientMessageId
            })
            .ToListAsync(ct);

        return new PagedResult<ChatMessageDto>(items, total, query.Page, query.PageSize);
    }

    public Task<Conversation?> GetForTicketAsync(int ticketId, CancellationToken ct = default)
        => Db.Conversations
             .Include(c => c.Participants)
             .FirstOrDefaultAsync(c => c.TicketId == ticketId, ct);

    public Task<ChatMessageDto?> GetMessageDtoAsync(int messageId, CancellationToken ct = default)
        => Db.ChatMessages.AsNoTracking()
             .Where(m => m.Id == messageId)
             .Select(m => new ChatMessageDto
             {
                 Id = m.Id,
                 ConversationId = m.ConversationId,
                 Body = m.Body,
                 SenderId = m.SenderId ?? 0,
                 SenderName = m.SenderNameSnapshot,
                 SentAt = m.SentAt,
                 IsSystemMessage = m.IsSystemMessage,
                 ClientMessageId = m.ClientMessageId
             })
             .FirstOrDefaultAsync(ct)!;

    public async Task MarkReadAsync(int conversationId, int userId, CancellationToken ct = default)
        => await Db.ConversationParticipants
            .Where(p => p.ConversationId == conversationId && p.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastReadAt, DateTime.UtcNow), ct);

    public Task<ChatMessage?> FindByClientMessageIdAsync(
        int conversationId, string clientMessageId, CancellationToken ct = default)
        => Db.ChatMessages.AsNoTracking()
             .FirstOrDefaultAsync(m => m.ConversationId == conversationId &&
                                       m.ClientMessageId == clientMessageId, ct);
}

// =========================================================================
// Reports
// =========================================================================

public interface IReportRepository
{
    Task<PagedResult<CategorySatisfactionDto>> GetCategorySatisfactionAsync(
        PagedQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<DailyTicketCountDto>> GetDailyVolumeAsync(
        DateTime from, DateTime to, int? departmentId, CancellationToken ct = default);
}

/// <summary>One day's ticket counts, for the trend chart.</summary>
public class DailyTicketCountDto
{
    public DateTime Day { get; init; }
    public int Created { get; init; }
    public int Resolved { get; init; }
}

public class ReportRepository : IReportRepository
{
    private readonly TicketHubDbContext _db;

    public ReportRepository(TicketHubDbContext db) => _db = db;

    /// <summary>
    /// Average satisfaction per category, for resolved tickets only.
    /// </summary>
    /// <remarks>
    /// Worth reading closely — it is a group, an aggregate, a sort and a page in one
    /// expression, and not one Rating row is ever loaded into memory.
    /// <para/>
    /// Note the shape: the <c>query</c> variable is built once and used <b>twice</b> — once
    /// for the count and once for the page. Because <c>IQueryable</c> is lazy, that is two
    /// SQL statements from one definition rather than two hand-written near-duplicates that
    /// drift apart the first time somebody edits one of them.
    /// </remarks>
    public async Task<PagedResult<CategorySatisfactionDto>> GetCategorySatisfactionAsync(
        PagedQuery query, CancellationToken ct = default)
    {
        var grouped = _db.Ratings
            .AsNoTracking()
            .Where(r => r.Ticket.Status == TicketStatus.Resolved || r.Ticket.Status == TicketStatus.Closed)
            .GroupBy(r => new
            {
                r.Ticket.CategoryId,
                CategoryName = r.Ticket.Category.Name,
                DepartmentName = r.Ticket.Department.Name
            })
            .Select(g => new CategorySatisfactionDto
            {
                CategoryId = g.Key.CategoryId,
                CategoryName = g.Key.CategoryName,
                DepartmentName = g.Key.DepartmentName,
                AverageStars = g.Average(r => (double)r.Stars),
                RatingsCount = g.Count()
            });

        var total = await grouped.CountAsync(ct);

        var items = await grouped
            .OrderByDescending(x => x.AverageStars)
            .ThenBy(x => x.CategoryId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<CategorySatisfactionDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<DailyTicketCountDto>> GetDailyVolumeAsync(
        DateTime from, DateTime to, int? departmentId, CancellationToken ct = default)
    {
        var tickets = _db.Tickets.AsNoTracking()
            .Where(t => t.CreatedAt >= from && t.CreatedAt < to);

        if (departmentId.HasValue)
        {
            tickets = tickets.Where(t => t.DepartmentId == departmentId.Value);
        }

        // t.CreatedAt.Date translates to SQL Server's CAST(... AS date), so the grouping
        // happens in the database. It also means this particular query cannot seek the index
        // on CreatedAt for the GROUP BY — but the WHERE above already narrowed the range,
        // so the scan is over a small slice. Context decides whether a non-sargable
        // expression matters.
        return await tickets
            .GroupBy(t => t.CreatedAt.Date)
            .Select(g => new DailyTicketCountDto
            {
                Day = g.Key,
                Created = g.Count(),
                Resolved = g.Count(t => t.ResolvedAt != null)
            })
            .OrderBy(x => x.Day)
            .ToListAsync(ct);
    }
}
