using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Common;

namespace TicketHub.Contracts.Categories;

/// <summary>A ticket category — "Pothole", "Street lighting", "Illegal dumping".</summary>
public class CategoryDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }

    public int DepartmentId { get; init; }

    /// <summary>
    /// Flattened from <c>Category.Department.Name</c>. The caller wants a label, not an
    /// object graph — and a projection pulls this in the same SELECT for free.
    /// </summary>
    public string DepartmentName { get; init; } = string.Empty;

    /// <summary>Hours until a ticket in this category is considered late.</summary>
    public int SlaHours { get; init; }

    public int TicketCount { get; init; }

    public AuditInfoDto Audit { get; init; } = new();
}

/// <summary>A trimmed category for dropdowns. Deliberately tiny.</summary>
/// <remarks>
/// The "fill a &lt;select&gt;" case needs two fields. Sending the full
/// <see cref="CategoryDto"/> — audit block, counts, description — for a hundred rows is
/// wasted bandwidth on every page load. Different use case, different DTO.
/// </remarks>
public class CategoryLookupDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public class CreateCategoryDto
{
    [Required]
    [StringLength(60, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Description { get; set; }

    /// <summary>Every category belongs to exactly one department that owns the work.</summary>
    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "A category must belong to a department.")]
    public int DepartmentId { get; set; }

    [Range(1, 720, ErrorMessage = "SLA must be between 1 hour and 30 days.")]
    public int SlaHours { get; set; } = 72;
}

public class UpdateCategoryDto
{
    [Required]
    [StringLength(60, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Description { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int DepartmentId { get; set; }

    [Range(1, 720)]
    public int SlaHours { get; set; } = 72;

    public bool IsActive { get; set; } = true;
}

public class CategoryQuery : PagedQuery
{
    public int? DepartmentId { get; set; }
    public bool? IsActive { get; set; }
}
