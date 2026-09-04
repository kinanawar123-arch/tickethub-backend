using System.ComponentModel.DataAnnotations;

namespace TicketHub.Contracts.Attachments;

/// <summary>A file hung off a ticket — usually the photo of the pothole.</summary>
/// <remarks>
/// WHAT WE STORE, AND WHAT WE DO NOT.
/// The bytes are on disk (see <c>FileStorageService</c>); the database holds only the
/// metadata and a path. Putting multi-megabyte blobs in SQL Server bloats every backup,
/// every restore and every accidental <c>SELECT *</c>. The database is very good at rows
/// and quite bad at being a filesystem.
/// </remarks>
public class AttachmentDto
{
    public int Id { get; init; }
    public int TicketId { get; init; }

    /// <summary>The name the user recognises, e.g. <c>pothole.jpg</c>.</summary>
    public string FileName { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }

    /// <summary>Where to download it from. We never expose the physical path on disk.</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    public int? UploadedById { get; init; }
    public string? UploadedByName { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Metadata that travels alongside the uploaded file in a multipart form.
/// </summary>
/// <remarks>
/// The file itself is bound as <c>IFormFile</c> in the controller, not here — a Contracts
/// project that referenced <c>IFormFile</c> would be dragging ASP.NET Core into a layer
/// that is supposed to be plain data.
/// </remarks>
public class UploadAttachmentMetadataDto
{
    [StringLength(200)]
    public string? Description { get; set; }
}
