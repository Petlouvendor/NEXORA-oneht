namespace Nexora.Contracts.Common;

/// <summary>Pagination contrôlée (Phase 3 §8, §24).</summary>
public sealed record PageRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    public const int MaxPageSize = 200;

    public (int Page, int Size) Normalized()
    {
        var page = Page < 1 ? 1 : Page;
        var size = PageSize is < 1 or > MaxPageSize ? 25 : PageSize;
        return (page, size);
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}
