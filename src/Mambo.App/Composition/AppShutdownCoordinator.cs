using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;

namespace Mambo.App.Composition;

public sealed class AppShutdownCoordinator(IPlaybackService playback, IMessenger messenger)
{
    private Task? closing;
    public Task CloseAsync() => closing ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        try
        {
            if (playback.Current is { } session) await session.CloseAsync();
        }
        finally { messenger.Reset(); }
    }
}
