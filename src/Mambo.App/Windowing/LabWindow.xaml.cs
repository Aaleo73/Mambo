using Mambo.App.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mambo.App.Windowing;

/// <summary>Codex 的 Video Lab / 假数据冒烟入口，供 scripts/test-*.ps1 使用。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WinUI 窗口的 Closed 事件释放 resizeHook。")]
public sealed partial class LabWindow : Window
{
    private readonly WindowResizeHook resizeHook;
    private bool closing;
    private bool finalClose;

    public LabWindow()
    {
        InitializeComponent();
        var presenter = OverlappedPresenter.Create();
        presenter.PreferredMinimumWidth = 1100;
        presenter.PreferredMinimumHeight = 720;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new SizeInt32(1500, 860));
        resizeHook = new WindowResizeHook(WinRT.Interop.WindowNative.GetWindowHandle(this), Lab.SetLiveResize);
        Lab.Initialize(this, presenter);
        Lab.SmokeCompleted += OnSmokeCompleted;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => resizeHook.Dispose();
    }

    /// <summary>冒烟脚本和 Video Lab 的启动参数。</summary>
    public static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Contains("--video-lab", StringComparer.Ordinal) ||
        arguments.Contains("--smoke", StringComparer.Ordinal) ||
        arguments.Contains("--fake-smoke", StringComparer.Ordinal);

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (finalClose) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    private async void OnSmokeCompleted() => await RequestCloseAsync();

    private async Task RequestCloseAsync()
    {
        if (closing) return;
        closing = true;
        // Lab.CloseAsync 先让 Closing 回调返回；重复关闭在清理完成前仍须取消。
        await Lab.CloseAsync();
        // Window.Close 直接销毁窗口，不保证触发 AppWindow.Closing。
        // 系统关闭与冒烟完成都必须先经过同一条异步清理路径。
        Lab.SmokeCompleted -= OnSmokeCompleted;
        AppWindow.Closing -= OnClosing;
        resizeHook.Dispose();
        finalClose = true;
        if (!DispatcherQueue.TryEnqueue(Close))
            throw new InvalidOperationException("无法排队关闭验证窗口。");
    }
}
