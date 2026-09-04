using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("Agents", t =>
            t.HasCheckConstraint("CK_Agents_MaxOpenTickets", "[MaxOpenTickets] > 0 AND [MaxOpenTickets] <= 200"));

        builder.HasKey(a => a.Id);

        builder.Property(a => a.FullName).IsRequired().HasMaxLength(120);
        builder.Property(a => a.MaxOpenTickets).HasDefaultValue(10);
        builder.Property(a => a.IsActive).HasDefaultValue(true);

        // ---------------------------------------------------------------------
        // 1:1 with the login — and the line that makes it 1:1
        // ---------------------------------------------------------------------
        //
        // A foreign key on its own gives you one-to-MANY: nothing stops two Agent rows
        // carrying the same UserId. Uniqueness on the FK is what makes it one-to-one.
        builder.HasIndex(a => a.UserId)
               .IsUnique()
               .HasDatabaseName("IX_Agents_UserId");

        builder.HasOne(a => a.User)
               .WithOne(u => u.Agent)
               .HasForeignKey<Agent>(a => a.UserId)
               // Restrict, not Cascade: deleting a login must not silently take an agent's
               // entire work history with it. Force the caller to decide.
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Department)
               .WithMany(d => d.Agents)
               .HasForeignKey(a => a.DepartmentId)
               .OnDelete(DeleteBehavior.Restrict);

        // ---------------------------------------------------------------------
        // N:N with skills — the case where EF writes the join table for you
        // ---------------------------------------------------------------------
        //
        // Two collections pointing at each other and no data on the pairing, so there is no
        // join class to write. Compare ConversationParticipant, which carries LastReadAt and
        // therefore has to be a real entity. That is the whole rule: does the join have
        // something to say for itself?
        builder.HasMany(a => a.Skills)
               .WithMany(s => s.Agents)
               .UsingEntity(j => j.ToTable("AgentSkills"));

        // "Who in this department still has capacity" — the auto-assignment query.
        builder.HasIndex(a => new { a.DepartmentId, a.IsActive })
               .HasDatabaseName("IX_Agents_Department_IsActive");
    }
}

/// <summary>
/// The 1:1 done the other way: the foreign key <em>is</em> the primary key.
/// </summary>
/// <remarks>
/// Compare <c>RatingConfiguration</c>, which gives the entity its own Id and adds a unique
/// index on the FK. Both are genuine 1:1s. Use shared-primary-key when the row is a pure
/// extension of its parent and has no identity of its own — as here.
/// </remarks>
public class AgentProfileConfiguration : IEntityTypeConfiguration<AgentProfile>
{
    public void Configure(EntityTypeBuilder<AgentProfile> builder)
    {
        builder.ToTable("AgentProfiles");

        // AgentId is the key. Two profiles for one agent is now physically impossible —
        // no extra index needed, because a primary key is already unique.
        builder.HasKey(p => p.AgentId);

        builder.Property(p => p.Biography).HasMaxLength(1000);
        builder.Property(p => p.AvatarUrl).HasMaxLength(300);
        builder.Property(p => p.OfficePhone).HasMaxLength(30);

        builder.HasOne(p => p.Agent)
               .WithOne(a => a.Profile)
               .HasForeignKey<AgentProfile>(p => p.AgentId)
               // Cascade: a profile with no agent is orphaned data with no way to reach it.
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("Skills");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).IsRequired().HasMaxLength(60);

        // Unique so you do not end up with "Electrical", "electrical" and "Electical".
        // SQL Server's default collation is case-insensitive, so this also catches the
        // second one — but do not rely on that. If this project ever moves to PostgreSQL,
        // that behaviour disappears and the duplicates come back.
        builder.HasIndex(s => s.Name).IsUnique().HasDatabaseName("IX_Skills_Name");
    }
}
