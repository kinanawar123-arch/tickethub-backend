using System.ComponentModel.DataAnnotations;

namespace TicketHub.Contracts.Ratings;

/// <summary>How the reporter scored the way their ticket was handled.</summary>
/// <remarks>
/// One rating per ticket — enforced by a UNIQUE index on <c>TicketId</c>, which is what turns
/// a 1:N into a genuine 1:1. Checking "does a rating already exist?" in C# is not enough:
/// two simultaneous requests can both pass that check, and only the index can stop both
/// from being written.
/// </remarks>
public class RatingDto
{
    public int Id { get; init; }
    public int TicketId { get; init; }

    /// <summary>1 to 5. Guarded by a CHECK constraint in the database as well as by validation here.</summary>
    public int Stars { get; init; }

    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? RatedByName { get; init; }
}

public class CreateRatingDto
{
    [Required]
    [Range(1, 5, ErrorMessage = "Rate between 1 and 5 stars.")]
    public int Stars { get; set; }

    [StringLength(500)]
    public string? Comment { get; set; }
}

/// <summary>One row of the "which categories are we good at" report.</summary>
public class CategorySatisfactionDto
{
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string DepartmentName { get; init; } = string.Empty;

    /// <summary>Average of <see cref="RatingDto.Stars"/>, computed by SQL's AVG — not in memory.</summary>
    public double AverageStars { get; init; }

    public int RatingsCount { get; init; }
}
