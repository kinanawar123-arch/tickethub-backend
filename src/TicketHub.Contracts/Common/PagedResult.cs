namespace TicketHub.Contracts.Common;

/// <summary>
/// One page of results plus enough information for the caller to draw a pager.
/// </summary>
/// <remarks>
/// WHY NOT JUST RETURN A LIST?
/// A bare list answers "here are 20 tickets" but not "out of how many". The front end
/// cannot render "Page 2 of 14" without <see cref="TotalCount"/>, and it would have to
/// call a second endpoint to get it. One shape, one round trip.
/// </remarks>
/// <typeparam name="T">The DTO being paged — never an entity.</typeparam>
public class PagedResult<T>
{
    /// <summary>The rows on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>How many rows match the filter in total, ignoring paging.</summary>
    public int TotalCount { get; init; }

    /// <summary>1-based. Page 1 is the first page, not page 0.</summary>
    public int Page { get; init; }

    /// <summary>How many rows were requested per page (already clamped by the service).</summary>
    public int PageSize { get; init; }

    /// <summary>Computed, not stored — there is no reason to let a caller send us a wrong one.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public PagedResult() { }

    public PagedResult(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>Handy for "no rows matched" without newing up an empty list at every call site.</summary>
    public static PagedResult<T> Empty(int page, int pageSize) =>
        new(Array.Empty<T>(), 0, page, pageSize);
}
