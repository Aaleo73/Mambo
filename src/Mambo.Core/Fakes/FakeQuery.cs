using Mambo.Core.Contracts;
using Mambo.Core.Data;
namespace Mambo.Core.Fakes;
public sealed class FakeQuery<T>(IUiScheduler scheduler, Func<CancellationToken, Task<T>> loader, CancellationToken scopeToken = default)
    : ObservableQuery<T>(scheduler, loader, scopeToken);
