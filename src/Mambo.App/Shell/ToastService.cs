using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Mambo.App.Shell;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed partial class ToastItem : ObservableObject
{
    internal ToastItem(ToastKind kind, string text, string? actionText, Action? action)
    {
        Kind = kind;
        Text = text;
        ActionText = actionText ?? "";
        Action = action;
    }

    public ToastKind Kind { get; }
    public string Text { get; }
    public string ActionText { get; }
    public bool HasAction => Action is not null;
    public bool IsInfo => Kind == ToastKind.Info;
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsError => Kind == ToastKind.Error;
    internal Action? Action { get; }
    internal DispatcherQueueTimer? Timer { get; set; }
    internal Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object>? TickHandler { get; set; }

    public string Glyph => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Warning => "",
        ToastKind.Error => "",
        _ => "",
    };
}

/// <summary>右下角通知，最多 3 条；错误需手动关闭，其余默认 3 秒消失，悬停时暂停计时。</summary>
public sealed class ToastService : IDisposable
{
    private const int Capacity = 3;
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);
    private DispatcherQueue? queue;

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Attach(DispatcherQueue dispatcher) => queue = dispatcher;

    public void Show(ToastKind kind, string text, string? actionText = null, Action? action = null, TimeSpan? duration = null)
    {
        var item = new ToastItem(kind, text, actionText, action);
        if (Items.Count >= Capacity)
        {
            var oldest = Items.FirstOrDefault(t => t.Kind != ToastKind.Error) ?? Items[0];
            Dismiss(oldest);
        }
        Items.Add(item);
        if (kind == ToastKind.Error || queue is null) return;
        item.Timer = queue.CreateTimer();
        item.Timer.Interval = duration ?? DefaultDuration;
        item.Timer.IsRepeating = false;
        item.TickHandler = (_, _) => Dismiss(item);
        item.Timer.Tick += item.TickHandler;
        item.Timer.Start();
    }

    public void Dismiss(ToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Timer?.Stop();
        if (item.Timer is { } timer && item.TickHandler is { } handler) timer.Tick -= handler;
        item.Timer = null;
        item.TickHandler = null;
        Items.Remove(item);
    }

    public void Dispose()
    {
        foreach (var item in Items.ToArray()) Dismiss(item);
        queue = null;
    }

    public void Invoke(ToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Dismiss(item);
        item.Action?.Invoke();
    }

    public static void Pause(ToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Timer?.Stop();
    }

    public static void Resume(ToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Timer?.Start();
    }
}
