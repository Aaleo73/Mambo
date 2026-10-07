using System.Diagnostics.CodeAnalysis;

namespace Mambo.Core.Contracts;

public enum SessionState
{
    Restoring,
    LoggedOut,
    LoggedIn,
    Unreachable,
}

/// <summary>公开账号身份，不包含地址、密码或访问令牌。</summary>
public sealed record SessionInfo(string ServerId, string UserId, string UserName);

public sealed record LogoutResult(bool RemoteLogoutFailed = false);

/// <summary>短时使用的登录输入；认证完成后不保留，不写入日志或持久化此对象。</summary>
public sealed class LoginRequest
{
    public LoginRequest(string serverAddress, string userName, string password)
    {
        ServerAddress = serverAddress;
        UserName = userName;
        Password = password;
    }

    public string ServerAddress { get; }
    public string UserName { get; }
    public string Password { get; }

    public override string ToString() => "LoginRequest { <redacted> }";
}

/// <summary>账号状态通知经 IUiScheduler 触发；命令失败抛出 AppException，主动取消抛出 OperationCanceledException。</summary>
public interface ISessionService
{
    SessionInfo? Current { get; }
    SessionState State { get; }
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error 与 docs/ARCHITECTURE.md §7 的读取错误约定保持一致，不用于跨语言调用。")]
    AppError? Error { get; }
    event EventHandler? Changed;
    Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task RestoreAsync(CancellationToken cancellationToken = default);
    /// <summary>先以旧账号结束播放与停止上报，再清除本地会话。前端事先确认；远端注销失败仍已本地断开。</summary>
    Task<LogoutResult> LogoutAsync(CancellationToken cancellationToken = default);
}
