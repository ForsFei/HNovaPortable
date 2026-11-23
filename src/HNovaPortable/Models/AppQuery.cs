namespace HNovaPortable.Models;

public enum AppSort
{
    Name = 0,
    RecentlyAdded = 1,
    RecentlyUsed = 2,
    LaunchCount = 3,
    Size = 4,
}

/// <summary>软件列表查询条件。所有条件是 AND 组合。</summary>
public sealed record AppQuery
{
    public string? CategoryId { get; init; }

    public bool FavoritesOnly { get; init; }

    public bool UnhealthyOnly { get; init; }

    public string? SearchText { get; init; }

    public string? TagName { get; init; }

    public AppSort Sort { get; init; } = AppSort.Name;

    public bool Descending { get; init; }
}
