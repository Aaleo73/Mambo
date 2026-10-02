using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Data;
namespace Mambo.Core.Fakes;
public sealed record FakePage<T>(ImmutableArray<T> Items, int? TotalCount, bool HasMore);
public sealed class FakePagedQuery<T>(IUiScheduler scheduler, Func<int, int, CancellationToken, Task<FakePage<T>>> loader, int pageSize = 60, CancellationToken scopeToken = default)
    : ObservablePagedQuery<T>(scheduler, async (offset, count, token) => { var page = await loader(offset, count, token).ConfigureAwait(false); return new QueryPage<T>(page.Items, page.TotalCount, page.HasMore); }, pageSize, scopeToken: scopeToken);
