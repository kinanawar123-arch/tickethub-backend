using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories", t =>
            t.HasCheckConstraint("CK_Categories_SlaHours", "[SlaHours] > 0 AND [SlaHours] <= 720"));

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
               .IsRequired()
               .HasMaxLength(60);

        builder.Property(c => c.Description).HasMaxLength(200);

        builder.Property(c => c.SlaHours).HasDefaultValue(72);
        builder.Property(c => c.IsActive).HasDefaultValue(true);

        // Composite unique index: category names must be unique WITHIN a department,
        // not globally. Roads and Parks may both legitimately have "Maintenance".
        //
        // Column order matters in a composite index. (DepartmentId, Name) can also serve a
        // query that filters on DepartmentId alone — an index is usable left-to-right, like
        // a phone book sorted by surname then first name. (Name, DepartmentId) could not.
        builder.HasIndex(c => new { c.DepartmentId, c.Name })
               .IsUnique()
               .HasDatabaseName("IX_Categories_Department_Name");

        builder.HasOne(c => c.Department)
               .WithMany(d => d.Categories)
               .HasForeignKey(c => c.DepartmentId)
               // Restrict: you may not delete a department that still owns categories.
               // The alternative — Cascade — means one careless DELETE takes a department,
               // its categories, and every ticket in them. Make the caller deal with the
               // children explicitly; an error message is cheaper than a restore.
               .OnDelete(DeleteBehavior.Restrict);
    }
}
