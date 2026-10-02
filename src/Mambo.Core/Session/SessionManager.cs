using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;

namespace Mambo.Core.Session;

/// <summary>串行处理账号生命周期；凭据只进入 ISecretStore，公开状态不包含地址与令牌。</summary>
public sealed class SessionManager : ISessionService, IDisposable
{
    private readonly EmbyApi api;
    private readonly ISecretStore secrets;
    private readonly AccountContext accounts;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly IPlaybackService? playback;
    private readonly TimeProvider clock;
    private readonly Func<AccountSession, CancellationToken, Task>? flush;
    private readonly Func<string, Task>? clear;
    private readonly object gate = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<AccountSession> pendingExpirations = [];
    private bool disposed;
    private bool lifetimeCancelled;
    private int activeOperations;
    private long revision;
    private SessionInfo? current;
    private SessionState state = SessionState.LoggedOut;
    private AppError? error;

    public SessionManager(EmbyApi api, ISecretStore secrets, AccountContext accounts, IUiScheduler scheduler,
        IMessenger messenger, IPlaybackService? playback = null, TimeProvider? clock = null,
        Func<AccountSession, CancellationToken, Task>? flush = null, Func<string, Task>? clear = null)
    {
        this.api = api;
        this.secrets = secrets;
        this.accounts = accounts;
        this.scheduler = scheduler;
        this.messenger = messenger;
        this.playback = playback;
        this.clock = clock ?? TimeProvider.System;
        this.flush = flush;
        this.clear = clear;
        api.AuthenticationExpired += OnAuthenticationExpired;
    }

    public SessionInfo? Current { get { lock (gate) return current; } }
    public SessionState State { get { lock (gate) return state; } }
    public AppError? Error { get { lock (gate) return error; } }
    public event EventHandler? Changed;

    public Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = ServerAddress.Normalize(request.ServerAddress);
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Length > 256)
            throw InvalidInput("请输入有效的用户名。");
        if (Encoding.UTF8.GetByteCount(request.Password) > 4096) throw InvalidInput("密码过长。");
        return RunCommandAsync(async token =>
        {
            if (accounts.Current is not null) throw new AppException(new(AppErrorKind.Auth,
                ErrorCodes.AlreadyLoggedIn, "请先断开当前账号，再连接其他账号。", false));
            var authentication = await api.AuthenticateAsync(address, request, token).ConfigureAwait(false);
            var secret = AuthenticationSecret(address, authentication, request.UserName.Trim());
            var saved = false;
            var installed = false;
            AccountSession? account = null;
            try
            {
                EnsureActive(token);
                await secrets.WriteAsync(secret, token).ConfigureAwait(false);
                saved = true;
                EnsureActive(token);
                account = new AccountSession(secret);
                InstallAccount(account, token);
                SetState(SessionState.LoggedIn, account, null, token);
                installed = true;
                StartBackgroundFlush(account);
            }
            finally
            {
                // 凭据已保存但登录在发布前被取消时，撤销这次未完成的登录。
                if (saved && !installed)
                {
                    if (account is not null && ReferenceEquals(accounts.Current, account)) accounts.Set(null);
                    await secrets.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default) => RunCommandAsync(async token =>
    {
        var account = accounts.Current;
        SessionState previousState;
        AppError? previousError;
        lock (gate) { previousState = state; previousError = error; }
        SetState(SessionState.Restoring, account, null, token);
        try
        {
            if (account is null)
            {
                var secret = await secrets.ReadAsync(token).ConfigureAwait(false);
                EnsureActive(token);
                if (secret is null)
                {
                    SetState(SessionState.LoggedOut, null, null, token);
                    return;
                }
                ValidateSecret(secret);
                account = new AccountSession(secret);
                InstallAccount(account, token);
                // 网络验证之前就安装账号，让账号作用域缓存可以恢复。
                SetState(SessionState.Restoring, account, null, token);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await api.ValidateAsync(account, token).ConfigureAwait(false);
                    EnsureActive(token);
                    SetState(SessionState.LoggedIn, account, null, token);
                    StartBackgroundFlush(account);
                    return;
                }
                catch (AppException exception) when (ShouldRetryRestore(exception.Error) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), clock, token).ConfigureAwait(false);
                }
            }
        }
        catch (AppException exception) when (exception.Error.Kind == AppErrorKind.Auth && account is not null)
        {
            await ExpireAccountAsync(account, exception.Error, token).ConfigureAwait(false);
            throw;
        }
        catch (AppException exception)
        {
            SetState(account is null ? SessionState.LoggedOut : SessionState.Unreachable, account, exception.Error, token);
            throw;
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            var failure = PersistenceFailure();
            SetState(account is null ? SessionState.LoggedOut : SessionState.Unreachable, account, failure.Error, token);
            throw failure;
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                if (disposed) throw;
            }
            SetState(account is null ? previousState : previousState == SessionState.LoggedOut ? SessionState.Unreachable : previousState,
                account, previousError, CancellationToken.None);
            throw;
        }
    }, cancellationToken);

    public Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default) => RunCommandAsync(async token =>
    {
        var account = accounts.Current;
        if (playback?.Current is { } active)
            await active.CloseAsync(PlaybackEndReason.Logout, token).ConfigureAwait(false);
        if (account is not null) await FlushBoundedAsync(account, token).ConfigureAwait(false);
        EnsureActive(token);
        await secrets.DeleteAsync(token).ConfigureAwait(false);
        // 凭据已经删除后，即使调用者此刻取消，也必须完成本地账号的断开。
        accounts.Set(null);
        SetState(SessionState.LoggedOut, null, null, CancellationToken.None);
        if (account is null) return new LogoutResult();
        AppException? cleanupFailure = null;
        try { if (clear is not null) await clear(account.Scope).ConfigureAwait(false); }
        catch (AppException exception) { cleanupFailure = exception; }
        catch (Exception exception) when (IsPersistenceFailure(exception)) { cleanupFailure = PersistenceFailure(); }
        var remoteFailed = false;
        try
        {
            // 本地注销已经提交，远端请求仍用旧账号身份。
            await api.LogoutAsync(account, token).ConfigureAwait(false);
        }
        catch (AppException) { remoteFailed = true; }
        if (cleanupFailure is not null) throw new AppException(cleanupFailure.Error);
        return new LogoutResult(RemoteLogoutFailed: remoteFailed);
    }, cancellationToken);

    private void InstallAccount(AccountSession account, CancellationToken token)
    {
        EnsureActive(token);
        accounts.Set(account);
        try { EnsureActive(token); }
        catch
        {
            if (ReferenceEquals(accounts.Current, account)) accounts.Set(null);
            throw;
        }
    }

    private void SetState(SessionState value, AccountSession? account, AppError? failure, CancellationToken token, bool expired = false)
    {
        SessionChanged message;
        long version;
        lock (gate)
        {
            ThrowIfStoppedLocked(token);
            state = value;
            current = account is null ? null : new SessionInfo(account.Secret.ServerId, account.Secret.UserId, account.Secret.UserName);
            error = failure;
            version = ++revision;
            message = new SessionChanged(state, current);
        }
        QueueOnUi(() =>
        {
            EventHandler? handler;
            lock (gate)
            {
                if (disposed || version != revision) return;
                handler = Changed;
            }
            handler?.Invoke(this, EventArgs.Empty);
            lock (gate) { if (disposed || version != revision) return; }
            messenger.Send(message);
            if (expired) messenger.Send(new SessionExpired());
        });
    }

    private void OnAuthenticationExpired(AccountSession account)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(accounts.Current, account) || !pendingExpirations.Add(account)) return;
        }
        _ = HandleAuthenticationExpiredAsync(account);
    }

    /// <summary>供图片等后端客户端报告当前账号认证失效；旧账号的迟到响应会被忽略。</summary>
    public void NotifyAuthenticationExpired(AccountSession account)
    {
        ArgumentNullException.ThrowIfNull(account);
        OnAuthenticationExpired(account);
    }

    private async Task HandleAuthenticationExpiredAsync(AccountSession account)
    {
        try
        {
            await RunCommandAsync(async token =>
            {
                if (!ReferenceEquals(accounts.Current, account)) return;
                await ExpireAccountAsync(account, ErrorText.Http(401, "恢复会话"), token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AppException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { lock (gate) pendingExpirations.Remove(account); }
    }

    private async Task ExpireAccountAsync(AccountSession account, AppError failure, CancellationToken token)
    {
        if (!ReferenceEquals(accounts.Current, account)) return;
        if (playback?.Current is { } active)
            await active.CloseAsync(PlaybackEndReason.Logout, token).ConfigureAwait(false);
        try { await secrets.DeleteAsync(token).ConfigureAwait(false); }
        finally
        {
            EnsureActive(token);
            if (ReferenceEquals(accounts.Current, account)) accounts.Set(null);
            SetState(SessionState.LoggedOut, null, failure, token, expired: true);
        }
        if (clear is not null) await clear(account.Scope).ConfigureAwait(false);
    }

    private void StartBackgroundFlush(AccountSession account)
    {
        if (flush is null) return;
        CancellationTokenSource linked;
        lock (gate)
        {
            if (disposed) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
            activeOperations++;
        }
        _ = FlushInBackgroundAsync(account, linked);
    }

    private async Task FlushInBackgroundAsync(AccountSession account, CancellationTokenSource linked)
    {
        try { await Task.Run(() => FlushBoundedAsync(account, linked.Token), linked.Token).ConfigureAwait(false); }
        catch (AppException) { }
        catch (OperationCanceledException) { }
        finally { linked.Dispose(); CompleteOperation(); }
    }

    private async Task FlushBoundedAsync(AccountSession account, CancellationToken token)
    {
        if (flush is null) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try { await flush(account, budget.Token).ConfigureAwait(false); }
        catch (AppException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
    }

    private Task<bool> RunCommandAsync(Func<CancellationToken, Task> command, CancellationToken token) =>
        RunCommandAsync(async linked => { await command(linked).ConfigureAwait(false); return true; }, token);

    private async Task<T> RunCommandAsync<T>(Func<CancellationToken, Task<T>> command, CancellationToken token)
    {
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            activeOperations++;
        }
        var entered = false;
        try
        {
            await commands.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            EnsureActive(linked.Token);
            return await command(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            throw PersistenceFailure();
        }
        finally
        {
            if (entered) commands.Release();
            linked.Dispose();
            CompleteOperation();
        }
    }

    private void EnsureActive(CancellationToken token)
    {
        lock (gate) ThrowIfStoppedLocked(token);
    }

    private void ThrowIfStoppedLocked(CancellationToken token)
    {
        if (disposed) throw new OperationCanceledException("账号服务已关闭。", token);
        token.ThrowIfCancellationRequested();
    }

    private void QueueOnUi(Action callback) => ThreadPool.QueueUserWorkItem(_ =>
    {
        lock (gate) { if (disposed) return; }
        scheduler.TryEnqueue(callback);
    });

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
            pendingExpirations.Clear();
        }
        api.AuthenticationExpired -= OnAuthenticationExpired;
        accounts.Set(null);
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

    private void CompleteOperation()
    {
        lock (gate)
        {
            activeOperations--;
            ReleaseResourcesIfIdleLocked();
        }
    }

    private void ReleaseResourcesIfIdleLocked()
    {
        if (!disposed || !lifetimeCancelled || activeOperations != 0) return;
        commands.Dispose();
        lifetime.Dispose();
    }

    private static bool ShouldRetryRestore(AppError failure) =>
        failure.Kind == AppErrorKind.Network || failure.Kind == AppErrorKind.Server && failure.Status is >= 500 and <= 599;

    private static SessionSecret AuthenticationSecret(ServerAddress address, EmbyAuthentication value, string fallbackName)
    {
        var secret = new SessionSecret(address.Uri.AbsoluteUri.TrimEnd('/'), value.ServerId ?? "", value.User?.Id ?? "",
            value.User?.Name ?? fallbackName, value.AccessToken ?? "");
        ValidateSecret(secret);
        return secret;
    }

    private static void ValidateSecret(SessionSecret value)
    {
        _ = ServerAddress.Normalize(value.ServerAddress);
        if (!ValidId(value.ServerId) || !ValidId(value.UserId) || value.UserName.Length > 256 ||
            string.IsNullOrWhiteSpace(value.AccessToken) || value.AccessToken.Any(char.IsControl))
            throw new AppException(ErrorText.InvalidResponse("恢复账号"));
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        Encoding.UTF8.GetByteCount(value) <= 256 && !value.Any(c => char.IsControl(c) || c is '"' or '\\');

    private static AppException InvalidInput(string text) => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, text, false));

    private static bool IsPersistenceFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or Win32Exception or JsonException;

    private static AppException PersistenceFailure() => new(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed,
        "无法访问本地账号数据，请检查权限后重试。", true));
}
