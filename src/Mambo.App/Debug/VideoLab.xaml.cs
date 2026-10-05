using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.Video;
using Mambo.App.Platform;
using Mambo.App.Composition;
using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using MpvValue = Mambo.Core.Playback.MpvValue;
using Mambo.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using HdrMode = Mambo.App.Video.HdrMode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace Mambo.App.Debug;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "宿主窗口通过 CloseAsync 解绑并释放 HDR 监听。")]
public sealed partial class VideoLab : UserControl
{
    private Window? window;
    private OverlappedPresenter? overlapped;
    private MpvCore? player;
    private LibMpvEngine? nativeEngine;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? playbackLifetime;
    private bool loaded;
    private bool qualityBusy;
    private VideoQualityMode confirmedQuality;
    private HdrController? hdr;
    private Task? consume;
    private Task? closeTask;
    private bool closing;
    private bool busy;
    private FakeLab? fakeLab;
    private bool seeking;
    private bool paused;
    private readonly ConcurrentDictionary<string, MpvValue> properties = new();
    private readonly DispatcherQueueTimer refresh;
    private readonly DispatcherQueueTimer bindingRetry;
    private readonly Stopwatch opening = new();
    private TaskCompletionSource? firstFrame;
    private int bindingAttempts;
    private long firstFrameMs;
    private long windowReadyMs;
    private bool bound;
    private string note = "";
    private readonly PowerRequest power = new();
    private ServiceProvider? backendServices;
    private IPlaybackSession? backendSession;
    internal event Action? SmokeCompleted;

    public VideoLab()
    {
        InitializeComponent();
        // Slider 的 Thumb 会处理 pointer 事件，因此要监听已处理事件。
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(SeekPressed), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(SeekReleased), true);
        Surface.PixelSizeRequested += (w, h) => player?.SetOutputSize(w, h);
        Surface.DiagnosticError += text => note = text;
        refresh = DispatcherQueue.CreateTimer();
        refresh.Interval = TimeSpan.FromMilliseconds(100);
        refresh.Tick += RefreshTick;
        bindingRetry = DispatcherQueue.CreateTimer();
        bindingRetry.Interval = TimeSpan.FromMilliseconds(300);
        bindingRetry.Tick += BindingRetryTick;
        Loaded += OnLoaded;
    }

    public void Initialize(Window owner, OverlappedPresenter presenter)
    {
        window = owner;
        overlapped = presenter;
        // 显示器监听属于窗口；每次播放只连接播放器，避免重复创建原生监听对象。
        hdr = new HdrController(owner.AppWindow.Id, DispatcherQueue);
    }
    public void SetLiveResize(bool active) { if (!closing) Surface.SetLiveResize(active); }
    private void RefreshTick(DispatcherQueueTimer sender, object args) => RefreshDiagnostics();
    private void BindingRetryTick(DispatcherQueueTimer sender, object args) => TryBind();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (closing || loaded) return;
        loaded = true;
        windowReadyMs = Program.UptimeMilliseconds;
        if (BackendServices.IsFakeMode(Program.Arguments, Environment.GetEnvironmentVariable("MAMBO_FAKE")))
        {
            Application.Current.UnhandledException += (_, failure) => FakeLab.RecordFailure(failure.Exception, "未处理的 UI 异常");
            fakeLab = new FakeLab();
            fakeLab.SmokeCompleted += () => SmokeCompleted?.Invoke();
            Content = fakeLab;
            return;
        }
        refresh.Start();
        if (Program.Arguments.Contains("--p7-smoke", StringComparer.Ordinal))
        {
            var ipcReport = Environment.GetEnvironmentVariable("MAMBO_EXTERNAL_LAB_REPORT") ?? "";
            Application.Current.UnhandledException += (_, failure) =>
            {
                // 仅合成管道诊断：记录退出阶段的类型/代码/调用栈，不复制异常消息。
                var safe = new ExternalIpcLabReport { Stage = "未处理的界面异常",
                    ErrorKind = failure.Exception.GetType().Name,
                    HResult = failure.Exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    ErrorStack = failure.Exception.StackTrace ?? "" };
                File.WriteAllText(ipcReport, JsonSerializer.Serialize(safe, ExternalIpcLabJsonContext.Default.ExternalIpcLabReport));
            };
            await ExternalIpcLabSmoke.RunAsync(ipcReport);
            SmokeCompleted?.Invoke(); return;
        }
        if (Program.Arguments.Contains("--p3-smoke", StringComparer.Ordinal))
        {
            var playbackSample = Environment.GetEnvironmentVariable("MAMBO_PLAYBACK_LAB_SAMPLE") ?? "";
            var playbackReport = Environment.GetEnvironmentVariable("MAMBO_PLAYBACK_LAB_REPORT") ?? "";
            Application.Current.UnhandledException += (_, failure) =>
            {
                var safe = new PlaybackLabReport { Stage = "未处理的界面异常", Error = "播放验证窗口出现异常。",
                    ErrorKind = failure.Exception.GetType().Name, HResult = failure.Exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    ErrorStack = failure.Exception.StackTrace ?? "" };
                File.WriteAllText(playbackReport, JsonSerializer.Serialize(safe, PlaybackLabJsonContext.Default.PlaybackLabReport));
            };
            await PlaybackLabSmoke.RunAsync(Surface, playbackSample, DispatcherQueue, playbackReport);
            SmokeCompleted?.Invoke(); return;
        }
        var sample = Environment.GetEnvironmentVariable("MAMBO_VIDEO_LAB_SAMPLE");
        if (Program.Arguments.Contains("--smoke", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(sample))
        {
            await RunSmokeAsync(sample);
            SmokeCompleted?.Invoke();
        }
        else if (!string.IsNullOrWhiteSpace(sample))
        {
            AddressBox.Text = sample;
            StatusText.Text = "HDR 样片已准备好，点击“打开”开始人工检查。";
        }
    }

    private async void PickFileClicked(object sender, RoutedEventArgs args)
    {
        if (window is null) return;
        try
        {
            var picker = new FileOpenPicker(window.AppWindow.Id);
            picker.FileTypeFilter.Add("*");
            var result = await picker.PickSingleFileAsync();
            if (result is not null) AddressBox.Text = result.Path;
        }
        catch { StatusText.Text = "文件选择器无法打开，请直接输入本地文件路径。"; }
    }

    private async void OpenClicked(object sender, RoutedEventArgs args) => await GuardAsync(
        () => OpenAsync(AddressBox.Text.Trim()));
    private async void RestoreBackendClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        var services = GetBackendServices();
        var sessionService = services.GetRequiredService<ISessionService>();
        await sessionService.RestoreAsync();
        StatusText.Text = sessionService.State == SessionState.LoggedIn ? "会话已恢复，可按 itemId 播放。" : "未能恢复会话，请在本机登录工具中连接 Emby。";
    });
    private ServiceProvider GetBackendServices()
    {
        if (backendServices is not null) return backendServices;
        backendServices = new ServiceCollection().AddBackendServices(false, new UiScheduler(DispatcherQueue)).BuildServiceProvider();
        _ = backendServices.GetRequiredService<AppShutdownCoordinator>();
        return backendServices;
    }
    private async void PlayItemClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        await StopAsync();
        var playback = GetBackendServices().GetRequiredService<IPlaybackService>();
        var opened = await playback.PlayAsync(new(ItemIdBox.Text.Trim(), ReplaceCurrent: true));
        backendSession = opened;
        opened.SnapshotChanged += BackendSnapshotChanged;
        Surface.Attach(opened);
        BackendSnapshotChanged(opened, EventArgs.Empty);
    });
    private void BackendSnapshotChanged(object? sender, EventArgs args)
    {
        if (sender != backendSession || backendSession is null) return;
        var snapshot = backendSession.Snapshot;
        var approvalNotice = backendServices?.GetRequiredService<ISettingsService>().ExternalPlayerStatus == ExternalPlayerStatus.Invalid
            ? "；外部 MPV 需要重新选择并批准，已切回内置播放。" : "";
        StatusText.Text = (snapshot.Error?.Message ?? $"播放状态：{snapshot.Phase}；位置 {TimeSpan.FromTicks(snapshot.PositionTicks):g}；倍速 {snapshot.PlaybackRate:F2}") + approvalNotice;
        power.SetPlaying(snapshot.Phase == PlayerPhase.Playing && !snapshot.IsPaused);
        if (!seeking) { PositionSlider.Maximum = Math.Max(1, TimeSpan.FromTicks(snapshot.DurationTicks).TotalSeconds);
            PositionSlider.Value = Math.Clamp(TimeSpan.FromTicks(snapshot.PositionTicks).TotalSeconds, 0, PositionSlider.Maximum); }
    }
    private async void PreviousItemClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    { if (backendSession is { } session) await session.PreviousAsync(); });
    private async void NextItemClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    { if (backendSession is { } session) await session.NextAsync(); });
    private async void RateItemClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    { if (backendSession is { } session) await session.SetRateAsync(1.5); });

    private async void PickExternalMpvClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        if (window is null) return;
        var path = await ExternalMpvPicker.PickAsync(window.AppWindow.Id);
        if (path is null) return;
        var settings = GetBackendServices().GetRequiredService<ISettingsService>();
        await settings.ValidateExternalPlayerAsync(path);
        await settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External });
        StatusText.Text = "外部播放器已批准，下次按 itemId 播放将使用外部窗口。";
    });
    private async void UseEmbeddedClicked(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        await GetBackendServices().GetRequiredService<ISettingsService>().UpdateAsync(value => value with { PlaybackMode = PlaybackMode.Embedded });
        StatusText.Text = "下次播放将使用内置画面。";
    });

    private async Task GuardAsync(Func<Task> action)
    {
        if (busy || closing) return;
        busy = true;
        OpenButton.IsEnabled = false;
        UpdateQualityButtons();
        try { await action(); }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (AppException ex) { StatusText.Text = ex.Error.Message; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException)
        {
            StatusText.Text = ex.Message;
        }
        catch { StatusText.Text = "视频验证操作失败，请关闭播放器后重试。"; }
        finally { busy = false; if (!closing) { OpenButton.IsEnabled = true; UpdateQualityButtons(); } }
    }

    private async Task OpenAsync(string address)
    {
        if (window is null || string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("请先选择本地文件或输入播放地址。");
        await StopAsync();
        lifetime.Token.ThrowIfCancellationRequested();
        if (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
            throw new InvalidOperationException("视频面板尚未完成布局，请稍后重试。");
        IReadOnlyDictionary<string, string>? headers = null;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (!string.IsNullOrWhiteSpace(TokenBox.Password) && !Uri.TryCreate(ServerBox.Text, UriKind.Absolute, out _))
                throw new InvalidOperationException("使用令牌时必须填写有效的 Emby 服务器地址。");
            using var client = StreamUrlResolver.CreateClient();
            var origin = Uri.TryCreate(ServerBox.Text, UriKind.Absolute, out var server) ? server : uri;
            var resolved = await new StreamUrlResolver(client).ResolveAsync(uri, origin, TokenBox.Password);
            address = resolved.Address.AbsoluteUri;
            headers = resolved.Headers;
            note = $"重定向 {resolved.Redirects} 次；携带认证头：{(headers.Count > 0 ? "是（同源）" : "否")}";
        }
        else if (!File.Exists(address) && !address.StartsWith("av://lavfi:", StringComparison.Ordinal))
            throw new InvalidOperationException("本地文件不存在。");
        opening.Restart();
        firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        firstFrameMs = 0;
        properties.Clear();
        bindingAttempts = 0;
        bound = false;
        var size = Surface.PixelSize;
        var enableAudio = !Program.Arguments.Contains("--no-audio", StringComparer.Ordinal);
        var playback = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        playbackLifetime = playback;
        var token = playback.Token;
        var created = await LibMpvEngine.CreateAsync(size.Width, size.Height, enableAudio: enableAudio, cancellationToken: token);
        if (closing || token.IsCancellationRequested || !ReferenceEquals(playbackLifetime, playback))
        {
            await created.DisposeAsync();
            throw new OperationCanceledException(token);
        }
        nativeEngine = created;
        player = created.Core;
        created.SwapChainChanged += NativeSwapChainChanged;
        consume = ConsumeAsync(created);
        try
        {
            await created.PrepareVideoQualityAsync(VideoQualityMode.Standard, token);
            confirmedQuality = VideoQualityMode.Standard;
            QualityStatusText.Text = "画质：等待首帧确认。";
            hdr!.Attach(created.Core);
            hdr.SetMode((HdrMode)HdrBox.SelectedIndex);
            await created.Core.LoadFileAsync(address, headers, token);
            StatusText.Text = "正在打开，等待首帧。";
            bindingRetry.Start();
        }
        catch { await StopAsync(); throw; }
    }

    private async Task ConsumeAsync(LibMpvEngine owner)
    {
        await foreach (var message in owner.Events.ReadAllAsync().ConfigureAwait(false))
        {
            if (message is EngineEvent.PropertyChanged property)
            {
                var name = property.Property switch
                {
                    EngineProperty.TimePosition => "time-pos", EngineProperty.Duration => "duration",
                    EngineProperty.Pause => "pause", EngineProperty.CoreIdle => "core-idle",
                    EngineProperty.HardwareDecoder => "hwdec-current", EngineProperty.AudioOutput => "current-ao",
                    EngineProperty.VideoParameters => "video-params", EngineProperty.VideoTargetParameters => "video-target-params",
                    _ => null,
                };
                if (name is not null && ReferenceEquals(nativeEngine, owner))
                {
                    if (property.Value is not null) properties[name] = property.Value;
                    else properties.TryRemove(name, out _);
                }
                continue;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (closing || nativeEngine != owner) return;
                switch (message)
                {
                    case EngineEvent.PlaybackRestart:
                        var initialFrame = firstFrameMs == 0;
                        firstFrameMs = firstFrameMs == 0 ? opening.ElapsedMilliseconds : firstFrameMs;
                        firstFrame?.TrySetResult();
                        StatusText.Text = "已开始播放。请检查画面和上方 XAML 按钮。";
                        if (initialFrame) QualityStatusText.Text = "当前画质：标准";
                        UpdateQualityButtons();
                        break;
                    case EngineEvent.EndFile { Reason: EngineEndReason.Error } end:
                        StatusText.Text = $"片源无法播放（代码 {end.Error}）。";
                        firstFrame?.TrySetException(new InvalidOperationException(StatusText.Text));
                        break;
                    case EngineEvent.Failure failure:
                        StatusText.Text = failure.Text;
                        break;
                    case EngineEvent.QueueOverflow:
                        note = "播放器事件队列溢出，请重新打开片源。";
                        break;
                }
            });
        }
    }

    private void NativeSwapChainChanged(MpvSwapChain reference) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing || nativeEngine is null) return;
        // 借用引用由引擎持有；UI 只绑定当前引擎的最新交换链，不释放事件引用。
        bound = false;
        bindingAttempts = 0;
        TryBind();
        if (!bound) bindingRetry.Start();
    });

    private void TryBind()
    {
        if (closing) return;
        if (nativeEngine is null) { bindingRetry.Stop(); return; }
        if (bound) { bindingRetry.Stop(); return; }
        if (++bindingAttempts > 10)
        {
            bindingRetry.Stop();
            StatusText.Text = "无法绑定视频交换链：10 次尝试已用尽，请重新打开。";
            firstFrame?.TrySetException(new InvalidOperationException(StatusText.Text));
            return;
        }
        var reference = nativeEngine.CurrentSwapChain;
        if (reference is null) return;
        if (reference.IsClosed || reference.IsInvalid) { Surface.Detach(); return; }
        try
        {
            Surface.Attach(reference.Address);
            bound = true;
            bindingRetry.Stop();
        }
        catch { note = $"视频交换链暂未就绪（第 {bindingAttempts} 次尝试）。"; }
    }

    private void RefreshDiagnostics()
    {
        if (closing) return;
        if (backendSession is { } session)
        {
            var engineName = session.Snapshot.EngineKind == EngineKind.External ? "外部 MPV" : "内置播放器";
            DiagnosticsText.Text = $"会话：{session.Snapshot.Phase}\n播放器：{engineName}\n画面：{Surface.BufferSize.Width} × {Surface.BufferSize.Height}\n倍速：{session.Snapshot.PlaybackRate:F2}";
            return;
        }
        var duration = Number("duration");
        var position = Number("time-pos");
        paused = properties.GetValueOrDefault("pause") is MpvValue.Flag { Value: true };
        power.SetPlaying(player is not null && !paused && firstFrameMs > 0 &&
            properties.GetValueOrDefault("core-idle") is not MpvValue.Flag { Value: true });
        if (!seeking)
        {
            PositionSlider.Maximum = Math.Max(1, duration);
            PositionSlider.Value = Math.Clamp(position, 0, PositionSlider.Maximum);
        }
        var buffer = Surface.BufferSize;
        var target = Surface.PixelSize;
        DiagnosticsText.Text = $"窗口就绪：{windowReadyMs} ms\n首帧：{firstFrameMs} ms\n" +
            $"面板像素：{target.Width} × {target.Height}\n缓冲区：{buffer.Width} × {buffer.Height}\n" +
            $"RasterizationScale：{Surface.DpiScale:F2}\n交换链已绑定：{bound}\n" +
            $"{hdr?.Description}\n时间：{position:F1} / {duration:F1}\n" +
            $"hwdec-current：{Format(properties.GetValueOrDefault("hwdec-current"))}\n" +
            $"video-params：\n{Format(properties.GetValueOrDefault("video-params"))}\n" +
            $"video-target-params：\n{Format(properties.GetValueOrDefault("video-target-params"))}\n{note}";
    }

    private double Number(string property) => properties.GetValueOrDefault(property) switch
    {
        MpvValue.Number value => value.Value, MpvValue.WholeNumber value => value.Value, _ => 0,
    };

    private static string Format(MpvValue? value) => value switch
    {
        MpvValue.Text text => text.Value,
        MpvValue.Number number => number.Value.ToString("G4", CultureInfo.InvariantCulture),
        MpvValue.WholeNumber integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        MpvValue.Flag flag => flag.Value ? "是" : "否",
        MpvValue.Map map => string.Join("\n", map.Values.Select(pair => $"{pair.Key}: {Format(pair.Value)}")),
        MpvValue.Array array => string.Join(", ", array.Values.Select(Format)),
        _ => "不可用",
    };

    private async void PauseClicked(object sender, RoutedEventArgs args)
    {
        if (backendSession is { } session) await GuardAsync(() => session.TogglePauseAsync());
        else player?.SetProperty("pause", !paused);
    }
    private void SeekPressed(object sender, PointerRoutedEventArgs args) => seeking = true;
    private async void SeekReleased(object sender, PointerRoutedEventArgs args)
    {
        seeking = false;
        if (backendSession is { } session) { await GuardAsync(() => session.SeekAsync(TimeSpan.FromSeconds(PositionSlider.Value))); return; }
        if (player is { } current)
            await GuardAsync(async () => { await current.CommandAsync("seek", PositionSlider.Value.ToString(CultureInfo.InvariantCulture), "absolute"); });
    }
    private async void HdrChanged(object sender, SelectionChangedEventArgs args)
    {
        if (HdrBox.SelectedIndex < 0) return;
        hdr?.SetMode((HdrMode)HdrBox.SelectedIndex);
        if (backendServices is { } services)
            await GuardAsync(() => services.GetRequiredService<ISettingsService>().UpdateAsync(value => value with { HdrMode = (Mambo.Core.Contracts.HdrMode)HdrBox.SelectedIndex }));
    }
    private void CursorChanged(object sender, RoutedEventArgs args) => Surface.HideCursor(((ToggleSwitch)sender).IsOn);
    private void FullscreenClicked(object sender, RoutedEventArgs args)
    {
        if (window is null) return;
        if (window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            window.AppWindow.SetPresenter(overlapped!);
        else window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }
    private void MaximizeClicked(object sender, RoutedEventArgs args)
    {
        if (window?.AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped || overlapped is not { } presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }
    private async void StopClicked(object sender, RoutedEventArgs args) => await GuardAsync(StopAsync);

    private void UpdateQualityButtons() => SetQualityButtonsEnabled(IsLoaded && !closing && !busy &&
        !qualityBusy && nativeEngine is not null && firstFrameMs > 0 && backendSession is null);

    private void SetQualityButtonsEnabled(bool enabled)
    {
        QualityStandardButton.IsEnabled = enabled;
        QualityClearButton.IsEnabled = enabled;
        QualityAnimeButton.IsEnabled = enabled;
    }

    private async void QualityClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { IsEnabled: true } || nativeEngine is not { } owner || playbackLifetime is not { } playback) return;
        var mode = ((sender as Button)?.Tag as string) switch
        {
            "Standard" => VideoQualityMode.Standard, "Clear" => VideoQualityMode.Clear,
            "Anime" => VideoQualityMode.Anime, _ => confirmedQuality,
        };
        var token = playback.Token;
        qualityBusy = true;
        UpdateQualityButtons();
        QualityStatusText.Text = "正在确认画质效果……";
        try
        {
            await owner.ApplyVideoQualityAsync(mode, token);
            if (closing || token.IsCancellationRequested || nativeEngine != owner) return;
            confirmedQuality = mode;
            QualityStatusText.Text = "当前画质：" + (mode switch
            {
                VideoQualityMode.Clear => "清晰", VideoQualityMode.Anime => "动画", _ => "标准",
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || closing) { }
        catch
        {
            if (!closing && nativeEngine == owner) QualityStatusText.Text = "画质切换失败，已恢复可用画质。";
        }
        finally
        {
            if (!closing && nativeEngine == owner) { qualityBusy = false; UpdateQualityButtons(); }
        }
    }
    private async void StressClicked(object sender, RoutedEventArgs args) => await GuardAsync(
        () => StressAsync(AddressBox.Text.Trim()));

    private async Task StressAsync(string sample, List<LifecycleSample>? measurements = null, int cycles = 20)
    {
        for (var i = 0; i < cycles; i++)
        {
            await OpenAsync(sample);
            await firstFrame!.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(100);
            await StopAsync();
            if (measurements is not null && (i == 0 || (i + 1) % 5 == 0))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                measurements.Add(new LifecycleSample
                {
                    Cycle = i + 1, Handles = process.HandleCount,
                    PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                    Threads = process.Threads.Count,
                    HandleTypes = HandleDiagnostics.Capture(),
                });
            }
            StatusText.Text = $"创建 / 销毁已完成 {i + 1} / {cycles} 次。";
        }
    }

    private async Task StopAsync()
    {
        var playback = playbackLifetime;
        playbackLifetime = null;
        playback?.Cancel();
        SetQualityButtonsEnabled(false);
        if (backendSession is { } session)
        {
            await session.CloseAsync();
            session.SnapshotChanged -= BackendSnapshotChanged;
            backendSession = null;
        }
        bindingRetry.Stop();
        hdr?.Detach();
        Surface.Detach(); // 必须先在 UI 线程解绑，再后台 quit / wait / destroy。
        bound = false;
        power.SetPlaying(false);
        var previous = nativeEngine;
        nativeEngine = null;
        player = null;
        if (previous is not null)
        {
            previous.SwapChainChanged -= NativeSwapChainChanged;
            await previous.DisposeAsync();
            if (consume is not null) await consume;
        }
        consume = null;
        playback?.Dispose();
        qualityBusy = false;
        if (!closing) QualityStatusText.Text = "画质：请先打开本地片源。";
    }

    public async Task CloseAsync()
    {
        // Closing 事件先返回给 WinUI，避免清理已完成时同步重入 Window.Close。
        await Task.Yield();
        await (closeTask ??= CloseCoreAsync());
    }
    private async Task CloseCoreAsync()
    {
        closing = true;
        lifetime.Cancel();
        Loaded -= OnLoaded;
        refresh.Stop();
        refresh.Tick -= RefreshTick;
        bindingRetry.Stop();
        bindingRetry.Tick -= BindingRetryTick;
        PositionSlider.RemoveHandler(PointerPressedEvent, new PointerEventHandler(SeekPressed));
        PositionSlider.RemoveHandler(PointerReleasedEvent, new PointerEventHandler(SeekReleased));
        if (fakeLab is { } demo) await demo.CloseAsync();
        if (backendServices is { } services)
        {
            await services.GetRequiredService<AppShutdownCoordinator>().CloseAsync();
            await services.DisposeAsync(); backendServices = null;
        }
        ItemIdBox.Text = "";
        AddressBox.Text = ServerBox.Text = TokenBox.Password = "";
        try { await StopAsync(); }
        catch { /* 释放有看门狗，窗口关闭不得永久等待。 */ }
        hdr?.Dispose();
        hdr = null;
        // 永久关闭时趁 HWND 仍有效释放 host clip、光标与计时器事件，不拖到原生卸载回调。
        Surface.Dispose();
        power.Dispose();
        lifetime.Dispose();
    }

    private async Task RunSmokeAsync(string sample)
    {
        var report = new VideoLabReport();
        try
        {
            report.Stage = "等待布局";
            var layoutDeadline = Environment.TickCount64 + 3000;
            while (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
            {
                if (Environment.TickCount64 >= layoutDeadline)
                    throw new TimeoutException("视频面板布局等待超时。");
                await Task.Delay(20);
            }
            report.Stage = "创建播放器";
            await OpenAsync(sample);
            report.Stage = "等待首帧";
            await firstFrame!.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(1500);
            report.WindowReadyMs = windowReadyMs;
            report.FirstFrameMs = firstFrameMs;
            report.HardwareDecoder = Format(properties.GetValueOrDefault("hwdec-current"));
            report.AudioOutput = Format(properties.GetValueOrDefault("current-ao"));
            report.SourceVideo = Format(properties.GetValueOrDefault("video-params"));
            report.TargetVideo = Format(properties.GetValueOrDefault("video-target-params"));
            report.Display = hdr?.Description ?? "";
            report.Bound = bound;
            report.Stage = "三模式画质渲染确认";
            var smokeEngine = nativeEngine ?? throw new InvalidOperationException("视频引擎未就绪。");
            var shaderFailures = smokeEngine.Core.ShaderFailureVersion;
            foreach (var mode in new[] { VideoQualityMode.Clear, VideoQualityMode.Anime, VideoQualityMode.Standard })
            {
                await smokeEngine.ApplyVideoQualityAsync(mode, playbackLifetime!.Token);
                report.ConfirmedVideoQualityModes.Add(mode.ToString());
            }
            report.VideoQualityModesVerified = report.ConfirmedVideoQualityModes.Count == 3 &&
                shaderFailures == smokeEngine.Core.ShaderFailureVersion;
            foreach (var size in new[] { new SizeInt32(1100, 720), new SizeInt32(1500, 860) })
            {
                report.Stage = "窗口尺寸";
                window!.AppWindow.Resize(size);
                await Task.Delay(1500);
                report.ResizeMatched &= Surface.PixelSize == Surface.BufferSize;
            }
            report.Stage = "最大化与全屏";
            overlapped!.Maximize();
            await Task.Delay(1200);
            window!.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            await Task.Delay(1200);
            report.FullscreenMatched = Surface.PixelSize == Surface.BufferSize;
            window.AppWindow.SetPresenter(overlapped);
            await StopAsync();
            report.Stage = "生命周期压力";
            var cycles = int.TryParse(Environment.GetEnvironmentVariable("MAMBO_VIDEO_LAB_CYCLES"), out var requested)
                ? Math.Clamp(requested, 1, 100) : 20;
            await StressAsync(sample, report.Lifecycle, cycles);
            report.Cycles = cycles;
            await Task.Delay(5000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh();
                report.Settled = new LifecycleSample
                {
                    Cycle = cycles, Handles = process.HandleCount, Threads = process.Threads.Count,
                    PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                    HandleTypes = HandleDiagnostics.Capture(),
                };
            }
            report.PipelinePassed = report.Bound && report.ResizeMatched && report.FullscreenMatched && report.VideoQualityModesVerified;
            if (report.Lifecycle.Count > 0 && report.Settled is { } final)
            {
                var initial = report.Lifecycle[0];
                report.ResourcesMeasured = initial.HandleTypes.ContainsKey("Section") && final.HandleTypes.ContainsKey("Section");
                report.ResourcesStable = report.ResourcesMeasured &&
                    final.HandleTypes.GetValueOrDefault("Section") <= initial.HandleTypes.GetValueOrDefault("Section") &&
                    final.HandleTypes.GetValueOrDefault("Mutant") <= initial.HandleTypes.GetValueOrDefault("Mutant") &&
                    final.PrivateBytes <= initial.PrivateBytes + 32 * 1024 * 1024;
            }
            report.Stage = "完成";
        }
        catch (Exception ex)
        {
            report.Error = ex is InvalidOperationException or TimeoutException ? ex.Message : "视频冒烟失败。";
            report.ErrorKind = ex.GetType().FullName ?? "";
        }
        finally
        {
            await CloseAsync();
            var path = Environment.GetEnvironmentVariable("MAMBO_VIDEO_LAB_REPORT");
            if (!string.IsNullOrWhiteSpace(path))
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, VideoLabJsonContext.Default.VideoLabReport));
        }
    }
}

internal sealed class VideoLabReport
{
    public bool PipelinePassed { get; set; }
    public bool ResourcesMeasured { get; set; }
    public bool ResourcesStable { get; set; }
    public bool Bound { get; set; }
    public bool ResizeMatched { get; set; } = true;
    public bool FullscreenMatched { get; set; }
    public bool VideoQualityModesVerified { get; set; }
    public List<string> ConfirmedVideoQualityModes { get; set; } = [];
    public int Cycles { get; set; }
    public long WindowReadyMs { get; set; }
    public long FirstFrameMs { get; set; }
    public string HardwareDecoder { get; set; } = "";
    public string AudioOutput { get; set; } = "";
    public string SourceVideo { get; set; } = "";
    public string TargetVideo { get; set; } = "";
    public string Display { get; set; } = "";
    public string Error { get; set; } = "";
    public string ErrorKind { get; set; } = "";
    public string Stage { get; set; } = "";
    public List<LifecycleSample> Lifecycle { get; set; } = [];
    public LifecycleSample? Settled { get; set; }
}

internal sealed class LifecycleSample
{
    public int Cycle { get; set; }
    public int Handles { get; set; }
    public int Threads { get; set; }
    public long PrivateBytes { get; set; }
    public long WorkingSetBytes { get; set; }
    public Dictionary<string, int> HandleTypes { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VideoLabReport))]
internal sealed partial class VideoLabJsonContext : JsonSerializerContext;
