using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

public class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("TicketComments");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Body).IsRequired().HasMaxLength(2000);
        builder.Property(c => c.AuthorNameSnapshot).IsRequired().HasMaxLength(120);
        builder.Property(c => c.IsInternal).HasDefaultValue(false);

        builder.HasOne(c => c.Author)
               .WithMany(u => u.Comments)
               .HasForeignKey(c => c.AuthorId)
               .OnDelete(DeleteBehavior.SetNull);

        // The comment list on a ticket detail page: filter by ticket, order by date.
        // A composite index that matches both the WHERE and the ORDER BY lets SQL Server
        // read the rows out already sorted, with no separate sort step at all.
        builder.HasIndex(c => new { c.TicketId, c.CreatedAt })
               .HasDatabaseName("IX_TicketComments_Ticket_CreatedAt");
    }
}

public class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.ToTable("TicketAttachments", t =>
            t.HasCheckConstraint("CK_TicketAttachments_Size", "[SizeBytes] > 0"));

        builder.HasKey(a => a.Id);

        builder.Property(a => a.FileName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.StoragePath).IsRequired().HasMaxLength(500);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(120);
        builder.Property(a => a.Description).HasMaxLength(200);

        builder.HasOne(a => a.UploadedBy)
               .WithMany()
               .HasForeignKey(a => a.UploadedById)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.TicketId).HasDatabaseName("IX_TicketAttachments_TicketId");
    }
}

/// <summary>
/// <see cref="TicketHistory"/> does not inherit <c>AuditableEntity</c>, so it does not get a
/// soft-delete filter automatically — but it has a <b>required</b> navigation to
/// <see cref="Ticket"/>, which does.
/// </summary>
/// <remarks>
/// EF warns about exactly this: a required navigation pointing at a filtered entity means a
/// history row whose ticket is invisible, which is a broken object. The fix is to give the
/// child the matching filter, which is what the <c>HasQueryFilter</c> below does. Delete that
/// line and you get warning <c>PossibleIncorrectRequiredNavigationWithQueryFilterInteraction</c>
/// on every build — now you know what it is telling you.
/// </remarks>
public class TicketHistoryConfiguration : IEntityTypeConfiguration<TicketHistory>
{
    public void Configure(EntityTypeBuilder<TicketHistory> builder)
    {
        builder.ToTable("TicketHistories");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Field).IsRequired().HasMaxLength(60);
        builder.Property(h => h.OldValue).HasMaxLength(400);
        builder.Property(h => h.NewValue).HasMaxLength(400);
        builder.Property(h => h.Note).HasMaxLength(500);
        builder.Property(h => h.ChangedByName).HasMaxLength(120);

        builder.HasOne(h => h.ChangedBy)
               .WithMany()
               .HasForeignKey(h => h.ChangedById)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(h => new { h.TicketId, h.ChangedAt })
               .HasDatabaseName("IX_TicketHistories_Ticket_ChangedAt");

        builder.HasQueryFilter(h => !h.Ticket.IsDeleted);
    }
}

public class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> builder)
    {
        builder.ToTable("Ratings", t =>
            t.HasCheckConstraint("CK_Ratings_Stars", "[Stars] >= 1 AND [Stars] <= 5"));

        builder.HasKey(r => r.Id);

        // THIS LINE is what turns a 1:N into a 1:1. Without it, nothing stops two rating
        // rows carrying the same TicketId — and an "if (already rated) return Conflict"
        // check in C# cannot stop two simultaneous requests from both passing it.
        builder.HasIndex(r => r.TicketId)
               .IsUnique()
               .HasDatabaseName("IX_Ratings_TicketId");

        builder.Property(r => r.Comment).HasMaxLength(500);

        builder.HasOne(r => r.Ticket)
               .WithOne(t => t.Rating)
               .HasForeignKey<Rating>(r => r.TicketId)
               // Cascade: a rating of a ticket that no longer exists is noise.
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.RatedByUser)
               .WithMany()
               .HasForeignKey(r => r.RatedByUserId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
