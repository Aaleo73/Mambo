using System.Diagnostics;
using System.Runtime.InteropServices;
using Mambo.App.Images;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT;

namespace Mambo.App.Debug;

/// <summary>
/// 在已打开的假电影资料库上采样。RenderingTime 只表示 XAML 的渲染回调时间，
/// 不表示 GPU 呈现时间；没有有效时间戳时明确返回 Unsupported。
/// </summary>
internal static class UiPerformanceProbe
{
    private const int ExpectedItems = 5000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ScrollWindow = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromMilliseconds(250);

    public static async Task<UiPerformanceReport> RunAsync(LibraryPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var report = new UiPerformanceReport();
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Budget);
        var token = deadline.Token;
        using var process = Process.GetCurrentProcess();
        var scroller = page.FindName("Scroller") as ScrollViewer;
        var repeater = page.FindName("Grid") as ItemsRepeater;
        var loader = ImageLoader.Current;
        var fetchBefore = loader?.FetchStartedCount;
        var hitBefore = loader?.DecodedHitCount;
        var originalOffset = scroller?.VerticalOffset ?? 0;
        Stopwatch? loadWatch = null;
        try
        {
            if (scroller is null || repeater is null || !page.IsLoaded || page.XamlRoot is null)
            {
                report.Status = "Unsupported";
                report.Reason = "真实资料库页面尚未挂载或缺少滚动网格。";
                return report;
            }
            report.ViewportWidth = scroller.ViewportWidth;
            report.ViewportHeight = scroller.ViewportHeight;
            report.RasterizationScale = page.XamlRoot.RasterizationScale;
            report.PrivateBytesBeforeLoad = ReadPrivateBytes(process);
            var cards = page.ViewModel.Cards;
            report.Stage = "WaitForLibrary";
            await WaitForReadyAsync(cards, token);
            if (cards.HasError)
            {
                report.Status = "Failed";
                report.Reason = "演示资料库首屏读取失败，已停止采样。";
                return report;
            }
            report.TotalItems = cards.TotalCount;
            if (cards.TotalCount != ExpectedItems)
            {
                report.Status = "Unsupported";
                report.Reason = "当前资料库或筛选结果不是 5000 项演示电影库。";
                return report;
            }

            report.Stage = "LoadMetadata";
            loadWatch = Stopwatch.StartNew();
            while (cards.Items.Count < ExpectedItems && cards.HasMore)
            {
                token.ThrowIfCancellationRequested();
                if (cards.HasError || cards.HasMoreError)
                {
                    report.Status = "Failed";
                    report.Reason = "演示资料库分页读取失败，已停止采样。";
                    return report;
                }
                var count = cards.Items.Count;
                await cards.LoadMoreAsync().WaitAsync(token);
                await Task.Yield();
                await WaitForReadyAsync(cards, token);
                if (count == cards.Items.Count && cards.HasMore)
                {
                    report.Status = "Failed";
                    report.Reason = "演示资料库分页没有追加内容，已停止采样。";
                    return report;
                }
                // 让真实集合绑定和布局处理追加页，不把所有元数据同步工作堆在一次回调里。
                await Task.Yield();
            }
            report.LoadMilliseconds = loadWatch.Elapsed.TotalMilliseconds;
            report.LoadedItems = cards.Items.Count;
            report.AllItemsLoaded = cards.Items.Count == ExpectedItems && !cards.HasMore;
            if (!report.AllItemsLoaded)
            {
                report.Status = "Failed";
                report.Reason = "演示资料库未完整加载 5000 项，已停止采样。";
                return report;
            }
            loadWatch.Stop();
            await Task.Delay(100, token);
            scroller.UpdateLayout();
            report.ScrollableHeight = scroller.ScrollableHeight;
            report.PrivateBytesAfterLoad = ReadPrivateBytes(process);
            report.PeakPrivateBytes = Math.Max(report.PrivateBytesBeforeLoad, report.PrivateBytesAfterLoad);
            if (scroller.ViewportHeight <= 0 || scroller.ScrollableHeight <= scroller.ViewportHeight)
            {
                report.Status = "Unsupported";
                report.Reason = "滚动视口或内容范围不足以采样。";
                return report;
            }

            var distance = Math.Min(scroller.ScrollableHeight, scroller.ViewportHeight * 5);
            var windows = new (string Name, double Start)[]
            {
                ("Top", 0),
                ("Middle", Math.Max(0, (scroller.ScrollableHeight - distance) / 2)),
                ("Bottom", Math.Max(0, scroller.ScrollableHeight - distance)),
            };
            var measurements = new List<UiScrollMeasurement>();
            foreach (var window in windows)
            {
                report.Stage = "Scroll" + window.Name;
                scroller.ChangeView(null, window.Start, null, true);
                await Task.Delay(250, token);
                scroller.UpdateLayout();
                var measurement = await MeasureWindowAsync(window.Name, window.Start, window.Start + distance,
                    scroller, repeater, process, loader, token);
                measurements.Add(measurement);
                report.Windows = measurements.ToArray();
                report.PeakPrivateBytes = Math.Max(report.PeakPrivateBytes, measurement.PeakPrivateBytes);
            }
            report.FrameTimingSupported = report.Windows.All(window => window.FrameTimingSupported);
            report.Status = report.FrameTimingSupported ? "Measured" : "Unsupported";
            report.Reason = report.FrameTimingSupported ? "" : "至少一个滚动窗口没有足够的有效渲染时间戳；不推算帧率。";
            report.Stage = "Complete";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            report.Status = "Failed";
            report.Reason = "性能采样超过 20 秒总预算，已在 " + report.Stage + " 阶段取消；不据此推断性能通过。";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            report.Status = "Failed";
            // 仅固定错误类型，不复制可能包含外部地址的服务错误文本。
            report.Reason = "性能采样在 " + report.Stage + " 阶段失败：" + error.GetType().Name;
        }
        finally
        {
            report.LoadedItems = page.ViewModel.Cards.Items.Count;
            if (loadWatch is not null) report.LoadMilliseconds = loadWatch.Elapsed.TotalMilliseconds;
            report.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (scroller is { IsLoaded: true }) scroller.ChangeView(null, originalOffset, null, true);
            report.PrivateBytesAfterScroll = ReadPrivateBytes(process);
            report.PeakPrivateBytes = Math.Max(report.PeakPrivateBytes, report.PrivateBytesAfterScroll);
            if (loader is not null)
            {
                report.ImageServiceCalls = loader.FetchStartedCount - fetchBefore!.Value;
                report.DecodedCacheHits = loader.DecodedHitCount - hitBefore!.Value;
            }
        }
        return report;
    }

    private static async Task WaitForReadyAsync(PagedCards cards, CancellationToken token)
    {
        while ((!cards.IsInitialized && !cards.HasError) || cards.IsLoadingMore)
            await Task.Delay(10, token);
    }

    private static async Task<UiScrollMeasurement> MeasureWindowAsync(string name, double start, double end,
        ScrollViewer scroller, ItemsRepeater repeater, Process process, ImageLoader? loader, CancellationToken token)
    {
        var report = new UiScrollMeasurement { Name = name, RequestedStartOffset = start, RequestedEndOffset = end };
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var intervals = new List<double>();
        TimeSpan? previousTimestamp = null;
        var unsupportedTimestamp = false;
        var nextSample = TimeSpan.Zero;
        var fetchBefore = loader?.FetchStartedCount;
        var hitBefore = loader?.DecodedHitCount;
        void Sample()
        {
            var visualChildren = VisualTreeHelper.GetChildrenCount(repeater);
            var realized = 0;
            var visible = 0;
            var visibleImages = 0;
            var viewport = new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight);
            for (var index = 0; index < visualChildren; index++)
            {
                if (VisualTreeHelper.GetChild(repeater, index) is not FrameworkElement element ||
                    repeater.GetElementIndex(element) < 0) continue;
                realized++;
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility != Visibility.Visible) continue;
                var bounds = element.TransformToVisual(scroller)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (bounds.Right <= viewport.Left || bounds.Left >= viewport.Right ||
                    bounds.Bottom <= viewport.Top || bounds.Top >= viewport.Bottom) continue;
                visible++;
                visibleImages += CountRemoteImages(element);
            }
            report.SampleCount++;
            report.PeakVisualChildren = Math.Max(report.PeakVisualChildren, visualChildren);
            report.PeakRealizedCards = Math.Max(report.PeakRealizedCards, realized);
            report.PeakVisibleCards = Math.Max(report.PeakVisibleCards, visible);
            report.PeakVisibleRemoteImages = Math.Max(report.PeakVisibleRemoteImages, visibleImages);
            report.PeakPrivateBytes = Math.Max(report.PeakPrivateBytes, ReadPrivateBytes(process));
            report.PeakActiveImageCalls = Math.Max(report.PeakActiveImageCalls, loader?.ActiveFetchCount ?? 0);
        }
        void OnRendering(object? sender, object args)
        {
            try
            {
                report.RenderingCallbacks++;
                try
                {
                    // An object-typed native event argument may use a base RCW in NativeAOT.
                    // Explicit QI preserves the actual RenderingTime instead of rejecting that projection.
                    var rendering = args.As<RenderingEventArgs>();
                    var timestamp = rendering.RenderingTime;
                    if (previousTimestamp is { } previous)
                    {
                        var delta = timestamp - previous;
                        if (delta > TimeSpan.Zero) intervals.Add(delta.TotalMilliseconds);
                        else if (delta < TimeSpan.Zero) unsupportedTimestamp = true;
                        else report.DuplicateTimestamps++;
                    }
                    previousTimestamp = timestamp;
                }
                catch (Exception error) when (error is InvalidCastException or ArgumentException or COMException)
                {
                    unsupportedTimestamp = true;
                }
                if (watch.Elapsed >= nextSample)
                {
                    Sample();
                    nextSample = watch.Elapsed + SamplePeriod;
                }
                var progress = Math.Clamp(watch.Elapsed.TotalMilliseconds / ScrollWindow.TotalMilliseconds, 0, 1);
                scroller.ChangeView(null, start + (end - start) * progress, null, true);
                if (progress >= 1) complete.TrySetResult();
            }
            catch (Exception error) { complete.TrySetException(error); }
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            // 启动一个真实滚动更新；后续位置只由真实渲染回调推进。
            scroller.ChangeView(null, Math.Min(end, start + 1), null, true);
            var watchdog = Task.Delay(ScrollWindow + TimeSpan.FromMilliseconds(400), token);
            var finished = await Task.WhenAny(complete.Task, watchdog);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(finished, complete.Task)) await complete.Task;
            else report.Reason = "渲染回调没有在采样窗口内驱动完成滚动。";
        }
        finally { CompositionTarget.Rendering -= OnRendering; }

        Sample();
        report.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
        report.ActualEndOffset = scroller.VerticalOffset;
        report.ScrollRangeCovered = report.ActualEndOffset - start >= (end - start) * 0.9;
        report.FrameTimingSupported = !unsupportedTimestamp && intervals.Count >= 10 && complete.Task.IsCompletedSuccessfully && report.ScrollRangeCovered;
        report.ValidFrameIntervals = intervals.Count;
        if (report.FrameTimingSupported)
        {
            intervals.Sort();
            report.MedianFrameMilliseconds = Percentile(intervals, 0.5);
            report.P95FrameMilliseconds = Percentile(intervals, 0.95);
            report.MaximumFrameMilliseconds = intervals[^1];
            report.IntervalsOver60HzBudget = intervals.Count(value => value > 1000d / 60);
        }
        else if (!report.ScrollRangeCovered) report.Reason = "真实滚动没有覆盖至少 90% 的请求范围，不据此报告帧间隔。";
        else if (report.Reason.Length == 0) report.Reason = "没有足够的单调 RenderingTime 时间戳，不计算帧间隔。";
        if (loader is not null)
        {
            report.ImageServiceCalls = loader.FetchStartedCount - fetchBefore!.Value;
            report.DecodedCacheHits = loader.DecodedHitCount - hitBefore!.Value;
        }
        return report;
    }

    private static int CountRemoteImages(DependencyObject parent)
    {
        var count = parent is RemoteImage ? 1 : 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            count += CountRemoteImages(VisualTreeHelper.GetChild(parent, index));
        return count;
    }

    private static long ReadPrivateBytes(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static double Percentile(List<double> sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * fraction) - 1, 0, sorted.Count - 1)];
}

/// <summary>仅保存数值与固定说明；可直接加入 UiLab 的 JSON 源生成上下文。</summary>
internal sealed class UiPerformanceReport
{
    public string Status { get; set; } = "Pending";
    public string Stage { get; set; } = "ValidatePage";
    public string Reason { get; set; } = "";
    public string FrameMetric { get; set; } = "XAML RenderingEventArgs.RenderingTime interval; not GPU presentation";
    public string Scope { get; set; } = "Local fake 5000-item movie library; 3 windows, 5 viewport heights each; no universal FPS guarantee";
    public string MemoryMetric { get; set; } = "Whole-process private bytes; sampled every 250 ms; no forced GC";
    public string ImageMetric { get; set; } = "IImageService.FetchAsync calls including byte-cache hits; decoded-cache hits counted separately";
    public int ExpectedItems { get; set; } = 5000;
    public int LoadedItems { get; set; }
    public int? TotalItems { get; set; }
    public bool AllItemsLoaded { get; set; }
    public bool FrameTimingSupported { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double LoadMilliseconds { get; set; }
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }
    public double RasterizationScale { get; set; }
    public double ScrollableHeight { get; set; }
    public long PrivateBytesBeforeLoad { get; set; }
    public long PrivateBytesAfterLoad { get; set; }
    public long PrivateBytesAfterScroll { get; set; }
    public long PeakPrivateBytes { get; set; }
    public long? ImageServiceCalls { get; set; }
    public long? DecodedCacheHits { get; set; }
    public UiScrollMeasurement[] Windows { get; set; } = [];
}

internal sealed class UiScrollMeasurement
{
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public double RequestedStartOffset { get; set; }
    public double RequestedEndOffset { get; set; }
    public double ActualEndOffset { get; set; }
    public bool ScrollRangeCovered { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool FrameTimingSupported { get; set; }
    public int RenderingCallbacks { get; set; }
    public int DuplicateTimestamps { get; set; }
    public int ValidFrameIntervals { get; set; }
    public double? MedianFrameMilliseconds { get; set; }
    public double? P95FrameMilliseconds { get; set; }
    public double? MaximumFrameMilliseconds { get; set; }
    public int? IntervalsOver60HzBudget { get; set; }
    public int SampleCount { get; set; }
    public int PeakVisualChildren { get; set; }
    public int PeakRealizedCards { get; set; }
    public int PeakVisibleCards { get; set; }
    public int PeakVisibleRemoteImages { get; set; }
    public int PeakActiveImageCalls { get; set; }
    public long PeakPrivateBytes { get; set; }
    public long? ImageServiceCalls { get; set; }
    public long? DecodedCacheHits { get; set; }
}
