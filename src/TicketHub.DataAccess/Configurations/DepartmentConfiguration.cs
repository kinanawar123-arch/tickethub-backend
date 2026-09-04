using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

/// <summary>
/// Fluent configuration for <see cref="Department"/>.
/// </summary>
/// <remarks>
/// WHY A SEPARATE FILE PER ENTITY?
/// The alternative is one <c>OnModelCreating</c> that grows to a thousand lines and that two
/// people cannot edit without a merge conflict. <c>ApplyConfigurationsFromAssembly</c> finds
/// these automatically, so adding a class is the whole registration step.
/// <para/>
/// ATTRIBUTES OR FLUENT API? Both work, and they produce an identical schema. The split we
/// use in this project:
/// <list type="bullet">
/// <item><b>Attributes</b> on DTOs — because they double as request validation and produce a
///       clean 400 before anything touches the database.</item>
/// <item><b>Fluent API</b> on entities — because it keeps the entity class readable, it can
///       express things attributes cannot (composite keys, indexes, delete behaviour, check
///       constraints), and it does not force the data layer's rules into a project that other
///       layers reference.</item>
/// </list>
/// </remarks>
public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
               .IsRequired()
               .HasMaxLength(80);

        // Uniqueness enforced by the DATABASE, not by an "if (exists)" check in C#.
        //
        // Two simultaneous requests can BOTH pass an if-check — they run before either one
        // has inserted. They cannot both satisfy a unique index. The C# check is still worth
        // having, because it turns a 500 into a friendly 400 for the 99% case; but the index
        // is what makes the rule actually true.
        builder.HasIndex(d => d.Name)
               .IsUnique()
               .HasDatabaseName("IX_Departments_Name");

        builder.Property(d => d.Description).HasMaxLength(300);
        builder.Property(d => d.ContactEmail).HasMaxLength(160);

        builder.Property(d => d.IsActive).HasDefaultValue(true);

        // Filtered index: only the live rows are indexed. Every list screen asks for active
        // departments, and there is no reason to make the index carry the retired ones.
        builder.HasIndex(d => d.IsActive)
               .HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("IX_Departments_IsActive");
    }
}
