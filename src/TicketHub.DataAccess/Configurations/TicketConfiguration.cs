using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

/// <summary>
/// The busiest configuration in the project. Worth reading line by line.
/// </summary>
public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets", t =>
        {
            // A CHECK constraint is the last line of defence. Validation attributes protect
            // the API; this protects the TABLE — from a migration script, a seeder, a bulk
            // import, or someone with SSMS open at 2am. Data outlives applications.
            t.HasCheckConstraint("CK_Tickets_Priority", "[Priority] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Tickets_Status", "[Status] BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_Tickets_ResolvedAfterCreated",
                "[ResolvedAt] IS NULL OR [ResolvedAt] >= [CreatedAt]");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TicketNumber)
               .IsRequired()
               .HasMaxLength(20);

        // The business identifier must be unique — it is what a citizen quotes on the phone.
        builder.HasIndex(t => t.TicketNumber)
               .IsUnique()
               .HasDatabaseName("IX_Tickets_TicketNumber");

        builder.Property(t => t.Title).IsRequired().HasMaxLength(120);
        builder.Property(t => t.Description).IsRequired().HasMaxLength(4000);
        builder.Property(t => t.ReporterName).IsRequired().HasMaxLength(80);
        builder.Property(t => t.ReporterPhone).HasMaxLength(30);
        builder.Property(t => t.ReporterEmail).HasMaxLength(160);
        builder.Property(t => t.LocationAddress).HasMaxLength(250);

        // Store enums as int (the default). The alternative is
        //   .HasConversion<string>()
        // which makes the table readable in SSMS at the cost of a wider column and a slower
        // index. We keep ints and pin the enum numbers in code — see TicketEnums.cs.
        builder.Property(t => t.Status).HasDefaultValue(Contracts.Enums.TicketStatus.Open);
        builder.Property(t => t.Priority).HasDefaultValue(Contracts.Enums.TicketPriority.Medium);

        // ---------------------------------------------------------------------
        // Concurrency
        // ---------------------------------------------------------------------

        // IsRowVersion() maps this to SQL Server's rowversion type: 8 bytes the server
        // maintains itself and bumps on every UPDATE. EF then adds the original value to the
        // WHERE clause of updates and deletes, so a row someone else already changed matches
        // zero rows and raises DbUpdateConcurrencyException instead of silently winning.
        builder.Property(t => t.RowVersion).IsRowVersion();

        // IsOverdue is a computed C# property. EF must not try to make a column of it —
        // and note that queries cannot use it either, because there is no SQL for a C#
        // property getter. Query code spells the condition out; see TicketRepository.
        builder.Ignore(t => t.IsOverdue);

        // ---------------------------------------------------------------------
        // Relationships
        // ---------------------------------------------------------------------

        builder.HasOne(t => t.Category)
               .WithMany(c => c.Tickets)
               .HasForeignKey(t => t.CategoryId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Department)
               .WithMany(d => d.Tickets)
               .HasForeignKey(t => t.DepartmentId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.AssignedAgent)
               .WithMany(a => a.AssignedTickets)
               .HasForeignKey(t => t.AssignedAgentId)
               // SetNull: if an agent record goes, their tickets survive and drop back into
               // the unassigned queue. Cascade here would delete the work along with the
               // worker, which is never what anybody meant.
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(t => t.CreatedByUser)
               .WithMany(u => u.ReportedTickets)
               .HasForeignKey(t => t.CreatedByUserId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(t => t.Comments)
               .WithOne(c => c.Ticket)
               .HasForeignKey(c => c.TicketId)
               // Cascade is right here: a comment has no meaning without its ticket.
               // And because SaveChanges turns deletes into soft deletes, "cascade" in this
               // project means the children are soft-deleted too. Nothing is really removed.
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Attachments)
               .WithOne(a => a.Ticket)
               .HasForeignKey(a => a.TicketId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.History)
               .WithOne(h => h.Ticket)
               .HasForeignKey(h => h.TicketId)
               .OnDelete(DeleteBehavior.Cascade);

        // ---------------------------------------------------------------------
        // Indexes — one per query the application actually runs
        // ---------------------------------------------------------------------
        //
        // An index is not free: it costs disk, and it costs time on every INSERT and UPDATE.
        // So you add them for the queries you really run, not for every column that might
        // one day appear in a WHERE. Each one below maps to a real screen.

        // The security filter. Runs on EVERY query an agent makes, so it goes first.
        builder.HasIndex(t => new { t.DepartmentId, t.Status })
               .HasDatabaseName("IX_Tickets_Department_Status");

        // "My tickets", newest first.
        builder.HasIndex(t => new { t.AssignedAgentId, t.Status })
               .HasDatabaseName("IX_Tickets_Agent_Status");

        // The default list ordering.
        builder.HasIndex(t => t.CreatedAt)
               .HasDatabaseName("IX_Tickets_CreatedAt");

        // The overdue report. Filtered so the index only carries rows that can still be late —
        // resolved, closed and cancelled tickets are the bulk of the table and never appear in it.
        //
        // NOTE THE PREDICATE. It says "[Status] < 3", not "[Status] NOT IN (3, 4, 5)".
        // SQL Server's filtered indexes accept a deliberately small grammar: simple
        // comparisons joined by AND. NOT IN, OR and expressions are all rejected with a
        // bare "Incorrect syntax near 'NOT'", which is not the most helpful message you
        // will ever read.
        //
        // It works here because the enum values are pinned in TicketEnums.cs and the three
        // unfinished states (Open=0, InProgress=1, OnHold=2) sort below the three finished
        // ones. That is a real coupling between the C# enum and this index — which is
        // exactly why those numbers are written out explicitly rather than left implicit.
        builder.HasIndex(t => t.DueAt)
               .HasFilter("[Status] < 3")
               .HasDatabaseName("IX_Tickets_DueAt_Unfinished");

        builder.HasIndex(t => t.CategoryId).HasDatabaseName("IX_Tickets_CategoryId");

        // Soft delete: nearly every query has WHERE IsDeleted = 0 bolted on by the global
        // filter, so the column is worth indexing.
        builder.HasIndex(t => t.IsDeleted).HasDatabaseName("IX_Tickets_IsDeleted");
    }
}
