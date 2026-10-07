using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    /// <summary>
    /// 物理输入要求本窗口全程在前台。这里只记录取得输入之后前台被谁拿走过，
    /// 供验收脚本区分“桌面被其他窗口打断”和“检查未通过”；不抢回前台，也不读取其他窗口的标题或内容。
    /// </summary>
    private sealed class ForegroundWatch : IDisposable
    {
        private readonly MainWindow window;
        private readonly UiInputProbe input;
        private readonly UiLabReport report;
        private readonly ForegroundReport foreground = new();
        private readonly DispatcherQueueTimer timer;
        private readonly long started = Stopwatch.GetTimestamp();

        internal ForegroundWatch(MainWindow window, UiInputProbe input, UiLabReport report)
        {
            this.window = window;
            this.input = input;
            this.report = report;
            report.Foreground = foreground;
            timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.IsRepeating = true;
            timer.Tick += OnTick;
            window.Activated += OnActivated;
            timer.Start();
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) foreground.Deactivations++;
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            if (input.DescribeForegroundHolder() is not { } holder) return;
            // 本进程自己的弹出窗口和切换瞬间的“无前台”不是外部打断，单独计数。
            if (holder == "none" || holder == "self" || holder.StartsWith("self/", StringComparison.Ordinal))
            {
                foreground.SelfOrNoneSamples++;
                return;
            }
            if (foreground.ForeignSamples++ != 0) return;
            foreground.FirstHolder = holder;
            foreground.FirstStage = report.Motion is { Passed: false, FailureKind.Length: 0 } motion && report.PlayerControls is null
                ? $"{report.Stage}/{motion.Stage}" : report.Stage;
            foreground.FirstAtMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= OnTick;
            window.Activated -= OnActivated;
        }
    }
}

internal sealed class ForegroundReport
{
    /// <summary>取得输入后收到的真实窗口失活次数。</summary>
    public int Deactivations { get; set; }
    /// <summary>每 100 ms 采样一次，前台属于其他进程的次数。</summary>
    public int ForeignSamples { get; set; }
    public int SelfOrNoneSamples { get; set; }
    /// <summary>第一次占用前台的进程名和窗口类，例如 explorer/Shell_TrayWnd。</summary>
    public string FirstHolder { get; set; } = "";
    public string FirstStage { get; set; } = "";
    public long FirstAtMilliseconds { get; set; }
}
