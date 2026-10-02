using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>仅内存的演示会话，不连接服务器或保存登录请求中的地址与密码。</summary>
public sealed class FakeSessionService(FakeOperation operation, IUiScheduler scheduler, IMessenger messenger) : ISessionService, IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private bool lifetimeCancelled;
    private int activeCommands;
    private long revision;
    private SessionInfo? current = DemoIdentity;
    private SessionState state = SessionState.LoggedIn;
    private AppError? error;

    public SessionInfo? Current { get { lock (gate) return current; } }
    public SessionState State { get { lock (gate) return state; } }
    public AppError? Error { get { lock (gate) return error; } }
    public event EventHandler? Changed;
    private static SessionInfo DemoIdentity => new("demo-server", "demo-user", "演示用户");

    public Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Length > 256)
            throw InvalidInput("demo.username", "请输入有效的用户名。");
        if (string.IsNullOrWhiteSpace(request.ServerAddress) || Encoding.UTF8.GetByteCount(request.ServerAddress) > 2048)
            throw InvalidInput("demo.address", "请输入有效的服务器地址。");
        if (Encoding.UTF8.GetByteCount(request.Password) > 4096)
            throw InvalidInput("demo.password", "密码过长。");

        // 异步操作只捕获用户名，不捕获登录请求或密码。
        var userName = request.UserName.Trim();
        return RunCommandAsync(async token =>
        {
            lock (gate)
            {
                ThrowIfStopped(token);
                if (current is not null)
                    throw InvalidInput("demo.already_logged_in", "请先断开当前账号，再连接其他账号。");
            }
            await operation.ExecuteAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfStopped(token);
                current = DemoIdentity with { UserName = userName };
                state = SessionState.LoggedIn;
                error = null;
                PublishLocked();
            }
        }, cancellationToken);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default) => RunCommandAsync(async token =>
    {
        SessionState previousState;
        AppError? previousError;
        lock (gate)
        {
            ThrowIfStopped(token);
            previousState = state;
            previousError = error;
            state = SessionState.Restoring;
            error = null;
            PublishLocked();
        }
        try
        {
            await operation.ExecuteAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfStopped(token);
                state = current is null ? SessionState.LoggedOut : SessionState.LoggedIn;
                PublishLocked();
            }
        }
        catch (Exception exception) when (exception is AppException or OperationCanceledException)
        {
            bool cancelled;
            lock (gate)
            {
                cancelled = disposed || token.IsCancellationRequested || exception is OperationCanceledException;
                if (!disposed)
                {
                    state = cancelled ? previousState : SessionState.Unreachable;
                    error = cancelled ? previousError : ((AppException)exception).Error;
                    PublishLocked();
                }
            }
            if (cancelled && exception is not OperationCanceledException)
                throw new OperationCanceledException("演示会话恢复已取消。", token);
            throw;
        }
    }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) => RunCommandAsync(token =>
    {
        lock (gate)
        {
            ThrowIfStopped(token);
            current = null;
            state = SessionState.LoggedOut;
            error = null;
            PublishLocked();
        }
        return Task.CompletedTask;
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
        if (disposed) throw new OperationCanceledException("演示会话已关闭。", token);
        token.ThrowIfCancellationRequested();
    }

    private void PublishLocked()
    {
        var version = ++revision;
        var message = new SessionChanged(state, current);
        scheduler.TryEnqueue(() =>
        {
            lock (gate)
            {
                if (disposed || version != revision) return;
                Changed?.Invoke(this, EventArgs.Empty);
                if (!disposed) messenger.Send(message);
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
            current = null;
            state = SessionState.LoggedOut;
            error = null;
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

    private static AppException InvalidInput(string code, string message) =>
        new(new AppError(AppErrorKind.Contract, code, message, false));
}
