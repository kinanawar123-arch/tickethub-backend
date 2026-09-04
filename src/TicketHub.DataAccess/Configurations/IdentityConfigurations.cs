using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

/// <summary>
/// Our additions to Identity's user table.
/// </summary>
/// <remarks>
/// Identity has already configured the primary key, the unique index on
/// <c>NormalizedUserName</c>, the index on <c>NormalizedEmail</c>, and all the relationships
/// to roles, claims and logins. We only configure what we added ourselves — do not re-declare
/// Identity's own mappings, because the two definitions will drift apart and the surprising
/// one will win.
/// </remarks>
public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(120);
        builder.Property(u => u.IsActive).HasDefaultValue(true);

        // Identity indexes NormalizedEmail but does not make it unique — it supports
        // configurations where two accounts share an email and differ by provider. We do not,
        // so we tighten it. One email, one account.
        builder.HasIndex(u => u.NormalizedEmail)
               .IsUnique()
               .HasDatabaseName("IX_Users_NormalizedEmail_Unique");

        builder.HasMany(u => u.RefreshTokens)
               .WithOne(t => t.User)
               .HasForeignKey(t => t.UserId)
               // Cascade: tokens are worthless without the account, and leaving them behind
               // means an orphaned credential — the exact thing you never want lying around.
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.Notifications)
               .WithOne(n => n.User)
               .HasForeignKey(n => n.UserId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.Property(r => r.Description).HasMaxLength(200);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Token).IsRequired().HasMaxLength(200);
        builder.Property(t => t.ReplacedByToken).HasMaxLength(200);
        builder.Property(t => t.RevokedReason).HasMaxLength(100);
        builder.Property(t => t.CreatedByIp).HasMaxLength(64);
        builder.Property(t => t.UserAgent).HasMaxLength(400);

        // Every refresh looks the token up by value, so this index is on the hot path of
        // every session renewal in the system. Unique as well, because a duplicate would be
        // a catastrophic bug and the index turns it into a loud error.
        builder.HasIndex(t => t.Token)
               .IsUnique()
               .HasDatabaseName("IX_RefreshTokens_Token");

        // "Revoke everything for this user" — logout-everywhere, and the panic button when
        // reuse is detected.
        builder.HasIndex(t => new { t.UserId, t.ExpiresAt })
               .HasDatabaseName("IX_RefreshTokens_User_ExpiresAt");

        // NOTE: RefreshToken has no soft delete. When a token dies it should be genuinely
        // gone from every lookup, and a cleanup job removes expired rows for real. Soft
        // delete is for data you might want back; a spent credential is not that.
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Title).IsRequired().HasMaxLength(150);
        builder.Property(n => n.Message).IsRequired().HasMaxLength(500);
        builder.Property(n => n.Link).HasMaxLength(300);

        builder.HasOne(n => n.Ticket)
               .WithMany()
               .HasForeignKey(n => n.TicketId)
               // SetNull, not Cascade: "your ticket was resolved" is still worth reading
               // after the ticket itself has been removed.
               .OnDelete(DeleteBehavior.SetNull);

        // The bell icon asks exactly one question — "what have I not read?" — on every page
        // load. A filtered index makes it read only the unread rows, which for an active user
        // is a handful out of thousands.
        builder.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt })
               .HasDatabaseName("IX_Notifications_User_Unread");
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        // long, not int. This table gets a row for every write in the system; int runs out
        // at about two billion, and the migration to widen a primary key on the biggest
        // table in the database is not a pleasant afternoon.
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.EntityId).IsRequired().HasMaxLength(60);
        builder.Property(a => a.UserName).HasMaxLength(160);
        builder.Property(a => a.IpAddress).HasMaxLength(64);

        // Changes is JSON of arbitrary length — no max, so it maps to nvarchar(max).
        builder.Property(a => a.Changes);

        // "Show me everything that happened to ticket 42" — the question you ask when
        // something has gone wrong and you need to reconstruct the story.
        builder.HasIndex(a => new { a.EntityName, a.EntityId })
               .HasDatabaseName("IX_AuditLogs_Entity");

        builder.HasIndex(a => a.OccurredAt).HasDatabaseName("IX_AuditLogs_OccurredAt");
    }
}
