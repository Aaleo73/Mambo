using System.Diagnostics.CodeAnalysis;

namespace Mambo.Core.Contracts;

public enum AppErrorKind
{
    Auth,
    Network,
    Server,
    Contract,
    Cancelled,
    Player,
    Persistence,
}

/// <summary>可向用户显示的安全错误；Message 使用中文，不含完整 URL 或凭据。</summary>
public sealed record AppError(
    AppErrorKind Kind,
    string Code,
    string Message,
    bool Retryable,
    string? Stage = null,
    int? Status = null,
    string? DiagnosticId = null);

/// <summary>命令失败携带安全错误，不保留可能包含敏感数据的传输异常。</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only safe AppError values may cross the frontend boundary.")]
public sealed class AppException : Exception
{
    public AppException(AppError error) : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    public AppError Error { get; }
}
