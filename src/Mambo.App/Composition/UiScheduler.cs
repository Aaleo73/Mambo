using Mambo.Core.Contracts;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Composition;

public sealed class UiScheduler(DispatcherQueue queue) : IUiScheduler
{
    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (queue.HasThreadAccess) { callback(); return true; }
        return queue.TryEnqueue(() => callback());
    }
}
