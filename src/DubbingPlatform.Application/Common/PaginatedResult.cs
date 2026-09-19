namespace DubbingPlatform.Application.Common;

/// <summary>
/// Paginated list envelope: <c>{ items, page, pageSize, total, hasMore }</c>
/// serialized camelCase. <c>HasMore</c> is <c>page * pageSize &lt; total</c>.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed record PaginatedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long Total,
    bool HasMore)
{
    /// <summary>
    /// Creates a page from a full item snapshot.
    /// </summary>
    public static PaginatedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, long total)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be >= 1.");
        }

        if (pageSize < 1 || pageSize > PaginationParams.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"PageSize must be in 1..{PaginationParams.MaxPageSize}.");
        }

        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Total must be >= 0.");
        }

        var hasMore = (long)page * pageSize < total;
        return new PaginatedResult<T>(items, page, pageSize, total, hasMore);
    }
}

/// <summary>
/// Pagination query parameters. Defaults <c>Page=1, PageSize=20</c>, max 100.
/// Bind from query: <c>?page=1&amp;pageSize=20</c>.
/// </summary>
public sealed class PaginationParams
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// Normalizes in place: clamps <c>Page</c> to &gt;= 1 and
    /// <c>PageSize</c> to 1..100.
    /// </summary>
    public void Normalize()
    {
        if (Page < 1)
        {
            Page = 1;
        }

        if (PageSize < 1)
        {
            PageSize = 1;
        }
        else if (PageSize > MaxPageSize)
        {
            PageSize = MaxPageSize;
        }
    }

    /// <summary>
    /// Gets the normalized (page, pageSize) pair without mutating.
    /// </summary>
    public (int Page, int PageSize) Normalized()
    {
        var page = Page < 1 ? 1 : Page;
        var pageSize = PageSize < 1 ? 1 : PageSize > MaxPageSize ? MaxPageSize : PageSize;
        return (page, pageSize);
    }

    /// <summary>
    /// Gets the number of rows to skip.
    /// </summary>
    public int Skip()
    {
        var (page, pageSize) = Normalized();
        return checked((page - 1) * pageSize);
    }
}
