using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.Composition;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Mambo.App.Debug;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "宿主 VideoLab.CloseAsync 调用 CloseAsync，解绑并释放服务容器与查询。")]
public sealed partial class FakeLab : UserControl
{
    private readonly ServiceProvider services;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILibraryService library;
    private readonly IPlaybackService playback;
    private readonly IQuery<ImmutableArray<MediaLibrary>> librariesQuery;
    private IPagedQuery<MediaItem>? mediaQuery;
    private IPlaybackSession? session;
    private Task? closeTask;
    private bool closed;
    public ObservableCollection<DemoLibraryRow> Libraries { get; } = [];
    public ObservableCollection<DemoMediaRow> Items { get; } = [];
    public event Action? SmokeCompleted;

    internal static void RecordFailure(Exception exception, string stage)
    {
        var path = Environment.GetEnvironmentVariable("MAMBO_FAKE_LAB_REPORT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var report = new FakeLabReport { Stage = stage, Error = "假数据界面异常。", ErrorKind = exception.GetType().FullName ?? "",
            ErrorCode = exception.HResult, Stack = exception.StackTrace ?? "" };
        File.WriteAllText(path, JsonSerializer.Serialize(report, FakeLabJsonContext.Default.FakeLabReport));
    }

    public FakeLab()
    {
        InitializeComponent();
        var registrations = new ServiceCollection();
        var options = ReadOptions();
        registrations.AddBackendServices(true, new UiScheduler(DispatcherQueue), options);
        services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        library = services.GetRequiredService<ILibraryService>();
        playback = services.GetRequiredService<IPlaybackService>();
        playback.SessionEnded += SessionEnded;
        librariesQuery = library.ObserveLibraries(lifetime.Token);
        librariesQuery.Updated += LibrariesUpdated;
        Loaded += OnLoaded;
    }

    private static FakeOptions ReadOptions()
        => FakeOptions.FromEnvironment(Environment.GetEnvironmentVariable("MAMBO_FAKE_DELAY_MS"), Environment.GetEnvironmentVariable("MAMBO_FAKE_FAILURE_RATE"));

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await GuardAsync(() => librariesQuery.RefreshAsync(lifetime.Token));
        LibrariesUpdated(librariesQuery, EventArgs.Empty);
        if (Program.Arguments.Contains("--fake-smoke", StringComparer.Ordinal)) await RunSmokeAsync();
    }

    private void LibrariesUpdated(object? sender, EventArgs args)
    {
        if (closed) return;
        if (librariesQuery.Error is { } error) StatusText.Text = error.Message;
        if (!librariesQuery.IsInitialized) return;
        if (Libraries.Count > 0) return;
        foreach (var item in librariesQuery.Current) Libraries.Add(new DemoLibraryRow(item));
        if (Libraries.Count > 0) LibraryBox.SelectedIndex = 0;
    }

    private async void LibraryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (closed || LibraryBox.SelectedItem is not DemoLibraryRow selected || library is null) return;
        if (mediaQuery is { } old) { old.Updated -= MediaUpdated; old.Dispose(); }
        Items.Clear();
        mediaQuery = library.ObserveLibrary(selected.Id, new LibraryQuery(), scopeToken: lifetime.Token);
        mediaQuery.Updated += MediaUpdated;
        var current = mediaQuery;
        await GuardAsync(() => current.RefreshAsync(lifetime.Token));
        MediaUpdated(current, EventArgs.Empty);
    }

    private void MediaUpdated(object? sender, EventArgs args)
    {
        if (closed || sender != mediaQuery || mediaQuery is null) return;
        var latest = mediaQuery.Items;
        // 保留已有项，下一页只追加；刷新改变内容时重新填充。
        if (latest.Length < Items.Count || !latest.Take(Items.Count).Select(item => item.Id).SequenceEqual(Items.Select(row => row.Item.Id))) Items.Clear();
        for (var index = Items.Count; index < latest.Length; index++) Items.Add(new DemoMediaRow(latest[index]));
        CountText.Text = $"已加载 {Items.Count} / {mediaQuery.TotalCount?.ToString(CultureInfo.InvariantCulture) ?? "未知"} 项；{(mediaQuery.IsLoading ? "加载中" : "就绪")}";
        if (mediaQuery.Error is { } error) StatusText.Text = error.Message;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (AppException ex) { if (!closed) StatusText.Text = ex.Error.Message; }
        catch { if (!closed) StatusText.Text = "演示操作失败，请重试。"; }
    }

    private async void MoreClicked(object sender, RoutedEventArgs args)
    {
        if (mediaQuery is { } query) await GuardAsync(() => query.LoadMoreAsync(lifetime.Token));
    }
    private async void RefreshClicked(object sender, RoutedEventArgs args)
    {
        if (mediaQuery is { } query) await GuardAsync(() => query.RefreshAsync(lifetime.Token));
        else await GuardAsync(() => librariesQuery.RefreshAsync(lifetime.Token));
    }
    private async void MediaClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not DemoMediaRow row) return;
        var item = row.Item;
        await GuardAsync(async () =>
        {
            if (playback.Current is { } previous) await previous.CloseAsync(lifetime.Token);
            var current = await playback.PlayAsync(new PlayRequest(item.Id), lifetime.Token);
            Attach(current);
            var image = item.Images.FirstOrDefault() ?? new ImageRef(item.Id, ImageKind.Primary);
            var bytes = await services.GetRequiredService<IImageService>().FetchAsync(image, 320, cancellationToken: lifetime.Token);
            if (closed || session != current) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes.ToArray());
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!closed && session == current) GeneratedImage.Source = bitmap;
        });
    }

    private async void PreviewClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        if (playback.Current is { } previous) await previous.CloseAsync(lifetime.Token);
        Attach(await playback.PreviewAsync(lifetime.Token));
    });

    private void Attach(IPlaybackSession current)
    {
        if (closed) return;
        if (session is not null) session.SnapshotChanged -= SnapshotUpdated;
        session = current;
        session.SnapshotChanged += SnapshotUpdated;
        DemoSurface.Attach(current);
        SnapshotUpdated(current, EventArgs.Empty);
    }

    private void SnapshotUpdated(object? sender, EventArgs args)
    {
        if (closed || sender != session || session is null) return;
        var state = session.Snapshot;
        PlaybackTitle.Text = state.Entry?.Title ?? "播放已结束";
        var status = state.Error?.Message ?? (state.IsBuffering ? "缓冲中" : state.IsPaused ? "暂停" : "播放中");
        PlaybackState.Text = $"{status} · {TimeSpan.FromTicks(state.PositionTicks):mm\\:ss} / {TimeSpan.FromTicks(state.DurationTicks):mm\\:ss} · 第 {state.CurrentEntryIndex + 1} / {state.Entries.Length} 项";
    }

    private void SessionEnded(object? sender, PlaybackSessionEventArgs args)
    {
        if (session != args.Session) return;
        session.SnapshotChanged -= SnapshotUpdated;
        session = null;
        DemoSurface.Detach();
        PlaybackState.Text = "播放已结束。";
    }
    private async void PauseClicked(object sender, RoutedEventArgs args) { if (session is { } current) await GuardAsync(() => current.TogglePauseAsync(lifetime.Token)); }
    private async void PreviousClicked(object sender, RoutedEventArgs args) { if (session is { } current) await GuardAsync(() => current.PreviousAsync(lifetime.Token)); }
    private async void NextClicked(object sender, RoutedEventArgs args) { if (session is { } current) await GuardAsync(() => current.NextAsync(lifetime.Token)); }
    private async void SeekClicked(object sender, RoutedEventArgs args) { if (session is { } current) await GuardAsync(() => current.SeekAsync(TimeSpan.FromTicks(current.Snapshot.PositionTicks) + TimeSpan.FromSeconds(30), lifetime.Token)); }
    private async void StopClicked(object sender, RoutedEventArgs args) { if (session is { } current) await GuardAsync(() => current.CloseAsync(lifetime.Token)); }

    public Task CloseAsync() => closeTask ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        closed = true;
        lifetime.Cancel();
        librariesQuery.Updated -= LibrariesUpdated;
        librariesQuery.Dispose();
        if (mediaQuery is { } query) { query.Updated -= MediaUpdated; query.Dispose(); }
        if (session is { } current) current.SnapshotChanged -= SnapshotUpdated;
        DemoSurface.Dispose();
        playback.SessionEnded -= SessionEnded;
        await services.GetRequiredService<AppShutdownCoordinator>().CloseAsync();
        await services.DisposeAsync();
        lifetime.Dispose();
    }

    private async Task RunSmokeAsync()
    {
        var report = new FakeLabReport();
        try
        {
            report.Stage = "分页";
            // 不依赖 catalog 的具体 ID：取确定性第一库。
            if (Libraries.Count > 0)
            {
                using var first = library.ObserveLibrary(Libraries[0].Id, new LibraryQuery(), scopeToken: lifetime.Token);
                await first.RefreshAsync(lifetime.Token);
                var firstCount = first.Items.Length;
                await first.LoadMoreAsync(lifetime.Token);
                report.PageCount = first.Items.Length;
                report.TotalCount = first.TotalCount ?? 0;
                report.PagingPassed = firstCount == 60 && report.PageCount == 120 && report.TotalCount == 5000;
                report.Stage = "图片";
                var bytes = await services.GetRequiredService<IImageService>().FetchAsync(new ImageRef(first.Items[0].Id, ImageKind.Primary), 320, cancellationToken: lifetime.Token);
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                report.ImagePassed = bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;
            }
            report.Stage = "假播放";
            var demo = await playback.PreviewAsync(lifetime.Token);
            Attach(demo);
            var deadline = Environment.TickCount64 + 5000;
            while (demo.Snapshot.Phase != PlayerPhase.Playing && Environment.TickCount64 < deadline) await Task.Delay(20, lifetime.Token);
            await demo.NextAsync(lifetime.Token);
            report.PlaybackPassed = demo.Snapshot.EngineKind == EngineKind.Demo && demo.Snapshot.Entries.Length == 12 && demo.Snapshot.CurrentEntryIndex == 1;
            report.SurfacePassed = DemoSurface.IsDemoAttached;
            report.Stage = "关闭";
            await demo.CloseAsync(lifetime.Token);
            report.Closed = playback.Current is null;
            using var process = Process.GetCurrentProcess();
            report.NativeMpvLoaded = process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Contains("libmpv", StringComparison.OrdinalIgnoreCase));
            report.Passed = report.PagingPassed && report.ImagePassed && report.PlaybackPassed && report.SurfacePassed && report.Closed && !report.NativeMpvLoaded;
            report.Stage = "完成";
        }
        catch (Exception ex) { report.Error = ex is AppException app ? app.Error.Message : "假数据冒烟失败。"; report.ErrorKind = ex.GetType().FullName ?? ""; }
        var path = Environment.GetEnvironmentVariable("MAMBO_FAKE_LAB_REPORT");
        if (!string.IsNullOrWhiteSpace(path)) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, FakeLabJsonContext.Default.FakeLabReport));
        await CloseAsync();
        SmokeCompleted?.Invoke();
    }
}

internal sealed class FakeLabReport
{
    public bool Passed { get; set; }
    public bool PagingPassed { get; set; }
    public bool ImagePassed { get; set; }
    public bool PlaybackPassed { get; set; }
    public bool SurfacePassed { get; set; }
    public bool Closed { get; set; }
    public int PageCount { get; set; }
    public int TotalCount { get; set; }
    public bool NativeMpvLoaded { get; set; }
    public string Stage { get; set; } = "";
    public string Error { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public int ErrorCode { get; set; }
    public string Stack { get; set; } = "";
}
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FakeLabReport))]
internal sealed partial class FakeLabJsonContext : JsonSerializerContext;
