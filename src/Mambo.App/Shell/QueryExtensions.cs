using System.Collections.Immutable;
using Mambo.Core.Contracts;

namespace Mambo.App.Shell;

public static class QueryExtensions
{
    /// <summary>未初始化的查询的 Current 是 default 数组（不是 null），这里统一成空数组。</summary>
    public static ImmutableArray<T> ItemsOrEmpty<T>(this IQuery<ImmutableArray<T>>? query) =>
        query is not null && !query.Current.IsDefault ? query.Current : [];
}
