namespace TicketHub.Contracts.Common;

/// <summary>
/// Base class for every "give me a filtered list" query object.
/// Bound straight from the query string, e.g. <c>?page=2&amp;pageSize=20</c>.
/// </summary>
/// <remarks>
/// WHY THE PRIVATE BACKING FIELD ON <see cref="PageSize"/>?
/// Because the value arrives from the internet. Somebody will eventually send
/// <c>?pageSize=1000000</c> — by accident with a script, or on purpose to see what happens.
/// Clamping in the property setter means every endpoint that inherits this is protected,
/// and no developer has to remember to check.
/// <para/>
/// This is defence in depth: the service layer clamps again. Two cheap guards beat one.
/// </remarks>
public abstract class PagedQuery
{
    /// <summary>The biggest page anyone is allowed to ask for.</summary>
    public const int MaxPageSize = 100;

    private int _pageSize = 20;
    private int _page = 1;

    /// <summary>1-based page number. Anything below 1 is treated as 1.</summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>Rows per page. Clamped to 1..<see cref="MaxPageSize"/>.</summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            < 1 => 1,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    /// <summary>Free-text search. What it searches is up to each repository.</summary>
    public string? Search { get; set; }

    /// <summary>How many rows to skip to reach this page. Used by <c>Skip()</c>.</summary>
    public int Skip => (Page - 1) * PageSize;
}
