using TicketHub.Contracts.Enums;

namespace TicketHub.DataAccess.Entities;

/// <summary>
/// The technical audit trail: one row per insert, update or soft-delete on an audited table.
/// </summary>
/// <remarks>
/// Written entirely by <c>TicketHubDbContext.SaveChangesAsync</c> — no service ever creates
/// one. That is the point: an audit trail that developers have to remember to write is an
/// audit trail with holes in it, and you always find the holes on the day you need it.
/// <para/>
/// Compare <see cref="TicketHistory"/>: that one is curated and shown to users; this one is
/// exhaustive and shown to nobody until someone asks "who changed this in March".
/// <para/>
/// A REAL COST WORTH KNOWING: this roughly doubles the number of INSERTs. For an internal
/// municipal system handling thousands of tickets a day that is nothing. For a table taking
/// ten thousand writes a second it is not, and you would move this to an append-only store or
/// SQL Server's own temporal tables instead. Know which situation you are in.
/// </remarks>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>The CLR entity name: "Ticket", "Category".</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The primary key of the row that changed, as text — keys are not all ints.</summary>
    public string EntityId { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    /// <summary>
    /// JSON of only the properties that actually changed, as
    /// <c>{"Status":{"old":"Open","new":"InProgress"}}</c>.
    /// </summary>
    /// <remarks>
    /// Only the changed ones. Storing whole before/after snapshots of every row is how an
    /// audit table quietly becomes the biggest thing in the database.
    /// </remarks>
    public string? Changes { get; set; }

    public int? UserId { get; set; }

    /// <summary>Frozen, so the log still reads correctly after the account is renamed or gone.</summary>
    public string? UserName { get; set; }

    public DateTime OccurredAt { get; set; }

    public string? IpAddress { get; set; }
}
