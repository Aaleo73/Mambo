using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>演示设置只保留在内存中，不读取或运行传入的外部播放器路径。</summary>
public sealed class FakeSettingsService(FakeOperation operation, IUiScheduler scheduler) : ISettingsService, IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private bool lifetimeCancelled;
    private int activeCommands;
    private long revision;
    private AppSettings current = new() { DeviceId = new Guid("77a0bcd7-6d18-4f44-a6d2-8c0943d3c4a1") };
    private ConnectionDefaults connectionDefaults = new();
    private ExternalPlayerStatus externalPlayerStatus = ExternalPlayerStatus.UsingEmbedded;

    public AppSettings Current { get { lock (gate) return current; } }
    public ConnectionDefaults ConnectionDefaults { get { lock (gate) return connectionDefaults; } }
    public ExternalPlayerStatus ExternalPlayerStatus { get { lock (gate) return externalPlayerStatus; } }
    public event EventHandler? Changed;

    public Task UpdateAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(settings.Volume) || settings.Volume is < 0 or > 100 || settings.DeviceId == Guid.Empty ||
            !Enum.IsDefined(settings.HdrMode) || !Enum.IsDefined(settings.PlaybackMode) || !Enum.IsDefined(settings.HardwareDecoding))
            throw InvalidInput("播放器设置无效。");
        return RunCommandAsync(async token =>
        {
            await operation.ExecuteAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfStopped(token);
                current = settings with { PlaybackMode = PlaybackMode.Embedded, ExternalMpvPath = null };
                externalPlayerStatus = ExternalPlayerStatus.UsingEmbedded;
                PublishLocked();
            }
        }, cancellationToken);
    }

    public Task SaveConnectionDefaultsAsync(ConnectionDefaults defaults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (Encoding.UTF8.GetByteCount(defaults.ServerAddress) > 2048 || defaults.UserName.Length > 256)
            throw InvalidInput("服务器表单输入过长。");
        return RunCommandAsync(async token =>
        {
            await operation.ExecuteAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfStopped(token);
                connectionDefaults = defaults;
                PublishLocked();
            }
        }, cancellationToken);
    }

    public Task ValidateExternalPlayerAsync(string path, CancellationToken cancellationToken = default) => RunCommandAsync(async token =>
    {
        ExternalPlayerStatus previous;
        lock (gate)
        {
            ThrowIfStopped(token);
            previous = externalPlayerStatus;
            externalPlayerStatus = ExternalPlayerStatus.Validating;
            PublishLocked();
        }
        try
        {
            await operation.ExecuteAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfStopped(token);
                externalPlayerStatus = ExternalPlayerStatus.Invalid;
                PublishLocked();
            }
        }
        catch
        {
            lock (gate)
            {
                if (!disposed)
                {
                    externalPlayerStatus = previous;
                    PublishLocked();
                }
            }
            throw;
        }
    }, cancellationToken);

    public Task ClearCacheAsync(CancellationToken cancellationToken = default) => RunCommandAsync(async token =>
    {
        await operation.ExecuteAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            ThrowIfStopped(token);
            PublishLocked();
        }
    }, cancellationToken);

    private async Task RunCommandAsync(Func<CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            activeCommands++;
        }
        var entered = false;
        try
        {
            await commands.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            linked.Token.ThrowIfCancellationRequested();
            await command(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) commands.Release();
            linked.Dispose();
            lock (gate)
            {
                activeCommands--;
                ReleaseResourcesIfIdleLocked();
            }
        }
    }

    private void ThrowIfStopped(CancellationToken token)
    {
        if (disposed) throw new OperationCanceledException("演示设置服务已关闭。", token);
        token.ThrowIfCancellationRequested();
    }

    private void PublishLocked()
    {
        var version = ++revision;
        scheduler.TryEnqueue(() =>
        {
            lock (gate)
            {
                if (!disposed && version == revision) Changed?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            revision++;
            connectionDefaults = new();
            current = current with { ExternalMpvPath = null, PlaybackMode = PlaybackMode.Embedded };
            externalPlayerStatus = ExternalPlayerStatus.UsingEmbedded;
            Changed = null;
        }
        try { lifetime.Cancel(); }
        finally
        {
            lock (gate)
            {
                lifetimeCancelled = true;
                ReleaseResourcesIfIdleLocked();
            }
        }
    }

    private void ReleaseResourcesIfIdleLocked()
    {
        if (!disposed || !lifetimeCancelled || activeCommands != 0) return;
        commands.Dispose();
        lifetime.Dispose();
    }

    private static AppException InvalidInput(string message) =>
        new(new AppError(AppErrorKind.Contract, "demo.settings", message, false));
}
