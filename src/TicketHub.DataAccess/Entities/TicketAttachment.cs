namespace TicketHub.DataAccess.Entities;

/// <summary>A file attached to a ticket. Usually the photo that proves the pothole exists.</summary>
public class TicketAttachment : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>The name the user recognises. Shown in the UI; never used to build a path.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Where the bytes actually live, relative to the configured upload root.
    /// </summary>
    /// <remarks>
    /// This is a generated GUID name, not the user's filename. Two reasons, both real:
    /// two people uploading <c>photo.jpg</c> must not overwrite each other, and a filename
    /// that arrived from the internet can contain <c>../../</c> and walk out of your upload
    /// folder into somewhere far more interesting. Never build a path from user input.
    /// </remarks>
    public string StoragePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string? Description { get; set; }

    // ----- Required 1:N with the ticket -----

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    // ----- Optional 1:N with the uploader -----

    public int? UploadedById { get; set; }
    public ApplicationUser? UploadedBy { get; set; }
}
