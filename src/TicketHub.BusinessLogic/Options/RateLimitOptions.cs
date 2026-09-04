using System.ComponentModel.DataAnnotations;

namespace TicketHub.BusinessLogic.Options;

/// <summary>
/// Rate-limit settings, so the numbers live in configuration rather than in code.
/// </summary>
/// <remarks>
/// This matters more than it looks. Development wants a generous limit so nobody locks
/// themselves out while iterating on a .http file; production wants a tight one. Hard-code the
/// numbers and you cannot have both without a rebuild.
/// </remarks>
public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Turn the whole thing off — useful in an integration-test run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per window, per client, on the auth endpoints.</summary>
    [Range(1, 10_000)]
    public int AuthPermitLimit { get; set; } = 10;

    /// <summary>Length of the window, in seconds.</summary>
    [Range(1, 3600)]
    public int AuthWindowSeconds { get; set; } = 60;

    /// <summary>The looser limit applied to everything else.</summary>
    [Range(1, 100_000)]
    public int GlobalPermitLimit { get; set; } = 300;

    [Range(1, 3600)]
    public int GlobalWindowSeconds { get; set; } = 60;
}
