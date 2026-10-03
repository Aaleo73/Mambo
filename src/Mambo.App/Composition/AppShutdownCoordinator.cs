using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core;
using Mambo.App.Platform;
using Microsoft.UI.Xaml;

namespace Mambo.App.Composition;

public sealed class AppShutdownCoordinator : IDisposable
{
    private readonly IPlaybackService playback;
    private readonly IMessenger messenger;
    private readonly BackendRuntime? backend;
    private readonly AppExceptionMonitor? exceptions;
    private Task? closing;
    public AppShutdownCoordinator(IPlaybackService playback, IMessenger messenger, BackendRuntime? backend = null)
    {
        this.playback = playback; this.messenger = messenger; this.backend = backend;
        if (backend is not null && Application.Current is { } application) exceptions = new(application, backend.Log);
    }
    public Task CloseAsync() => closing ??= CloseCoreAsync();
    public void ReportPageFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (closing is null) backend?.Log.Exception("页面边界异常", error);
    }
    private async Task CloseCoreAsync()
    {
        try
        {
            if (backend is not null) await backend.DisposeAsync();
            else if (playback.Current is { } session) await session.CloseAsync(PlaybackEndReason.AppShutdown);
        }
        finally { Dispose(); messenger.Reset(); }
    }
    public void Dispose() { exceptions?.Dispose(); GC.SuppressFinalize(this); }
}
