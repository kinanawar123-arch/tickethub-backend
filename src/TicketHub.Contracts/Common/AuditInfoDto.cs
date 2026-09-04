namespace TicketHub.Contracts.Common;

/// <summary>
/// The audit block that every "detail" DTO in this project carries.
/// </summary>
/// <remarks>
/// Every table inherits <c>AuditableEntity</c> (CreatedAt / CreatedBy / UpdatedAt / UpdatedBy),
/// so rather than repeating four properties on twelve DTOs we group them once, here.
/// <para/>
/// Note that we expose the display *name* of the user, not just the id. The front end wants
/// "Created by Sara Khoury", and making it fetch every user by id to render a list is exactly
/// the N+1 problem we spend a whole lesson avoiding.
/// </remarks>
public class AuditInfoDto
{
    public DateTime CreatedAt { get; init; }
    public int? CreatedById { get; init; }
    public string? CreatedByName { get; init; }

    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedById { get; init; }
    public string? UpdatedByName { get; init; }
}
