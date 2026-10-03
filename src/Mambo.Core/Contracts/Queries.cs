using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Mambo.Core.Contracts;

/// <summary>始终异步排队到 UI 线程，禁止内联回调；返回 false 表示 UI 正在关闭。服务在回调时不持锁。</summary>
public interface IUiScheduler
{
    bool TryEnqueue(Action callback);
}

/// <summary>
/// 可释放的缓存观察。在 IsInitialized 为 true 之前，Current 不能用作已加载数据。
/// 刷新保留旧值，失败仅更新 Error。Updated 经 IUiScheduler 触发。
/// Dispose 结束当前观察，不清除共享缓存。主动取消抛出 OperationCanceledException，
/// 不将取消写入 Error；空结果仍然属于已初始化状态。共享刷新中，各调用者的取消
/// 仅结束自己的等待；底层读取由观察的 scopeToken 与 Dispose 取消。
/// </summary>
public interface IQuery<out T> : IDisposable
{
    T? Current { get; }
    bool IsInitialized { get; }
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error 是 PLAN §14 明确约定的 C# 契约成员名。")]
    AppError? Error { get; }
    bool IsRefreshing { get; }
    event EventHandler? Updated;
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Items 是已加载页的不可变快照，初次加载前为空；TotalCount 可未知。
/// 刷新成功后才用第一页替换 Items。分页失败保留已有条目，允许重试。
/// 并发 LoadMore 共享同一次分页操作。Updated 经 IUiScheduler 触发。
/// Dispose 取消当前观察的工作；主动取消抛出 OperationCanceledException，不覆盖 Error。
/// 各调用者的取消仅结束自己的等待，底层共享分页由 scopeToken 与 Dispose 取消。
/// </summary>
public interface IPagedQuery<T> : IDisposable
{
    ImmutableArray<T> Items { get; }
    int? TotalCount { get; }
    bool IsInitialized { get; }
    bool HasMore { get; }
    bool IsLoading { get; }
    bool IsRefreshing { get; }
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error 是 PLAN §14 明确约定的 C# 契约成员名。")]
    AppError? Error { get; }
    event EventHandler? Updated;
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task LoadMoreAsync(CancellationToken cancellationToken = default);
}
