using System.Collections.Immutable;

namespace Mambo.Core.Contracts;

public enum LibrarySort
{
    DateCreated,
    Name,
    CommunityRating,
    ProductionYear,
    Runtime,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>同一筛选组内取“或”，不同组之间取“与”。</summary>
public sealed record LibraryQuery
{
    public LibrarySort Sort { get; init; } = LibrarySort.DateCreated;
    public SortDirection Direction { get; init; } = SortDirection.Descending;
    public ImmutableArray<string> Genres { get; init; } = [];
    public ImmutableArray<int> Years { get; init; } = [];
    public ImmutableArray<string> OfficialRatings { get; init; } = [];
}

public sealed record FilterOptions
{
    public ImmutableArray<string> Genres { get; init; } = [];
    public ImmutableArray<int> Years { get; init; } = [];
    public ImmutableArray<string> OfficialRatings { get; init; } = [];
}

/// <summary>有序的搜索分组信息，每组分别创建自己的分页观察。</summary>
public sealed record SearchGroup(string LibraryId, string Name, int Order);

public enum ImagePriority
{
    Hero,
    Visible,
    Prefetch,
}
