using System.Globalization;
using System.Threading.Channels;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using EngineValue = Mambo.Core.Playback.MpvValue;

namespace Mambo.Player.LibMpv;

/// <summary>每个会话一个实例；调用 DisposeAsync 前，App 必须先在 UI 线程解绑交换链。</summary>
public sealed class LibMpvEngine : IPlayerEngine, IVideoQualityEngine
{
    // 事件转发循环之外，画质失效通知也会从后台线程写入。
    private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task forwarding;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private MpvSwapChain? currentSwapChain;
    private double videoAspect;
    private readonly VideoQualityController videoQuality;

    public EngineKind Kind => EngineKind.Embedded;
    public ChannelReader<EngineEvent> Events => events.Reader;

    // 只供 App 的视频平台桥接使用；前端不能依赖此类。
    public MpvCore Core { get; }
    /// <summary>由 Core 持有的借用引用；订阅者不要 Dispose。面板绑定会持有自己的 COM 引用。</summary>
    public MpvSwapChain? CurrentSwapChain => Volatile.Read(ref currentSwapChain);
    /// <summary>在后台事件转发线程触发，App 必须切回 UI 线程后绑定。</summary>
    public event Action<MpvSwapChain>? SwapChainChanged;
    /// <summary>最近一次有效的显示比例；尚未取得时为 0，起播与切集期间保留旧值。</summary>
    public double VideoAspect => Volatile.Read(ref videoAspect);
    /// <summary>在后台事件转发线程触发，App 必须切回 UI 线程后布局。</summary>
    public event Action<double>? VideoAspectChanged;

    private LibMpvEngine(MpvCore core, Func<VideoQualityMode, string[]>? videoQualityResources)
    {
        Core = core;
        videoQuality = new(core, videoQualityResources);
        videoQuality.Lost += () => events.Writer.TryWrite(new EngineEvent.VideoQualityLost());
        forwarding = ForwardEventsAsync();
    }

    public static Task<LibMpvEngine> CreateAsync(int pixelWidth = 1280, int pixelHeight = 720,
        bool headless = false, bool enableAudio = true, IReadOnlyDictionary<string, string>? optionOverrides = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(null, pixelWidth, pixelHeight, headless, enableAudio, optionOverrides, cancellationToken);

    // 测试注入着色器资源，在真实 GPU 上验证失败路径。
    internal static async Task<LibMpvEngine> CreateAsync(Func<VideoQualityMode, string[]>? videoQualityResources,
        int pixelWidth, int pixelHeight, bool headless, bool enableAudio,
        IReadOnlyDictionary<string, string>? optionOverrides, CancellationToken cancellationToken)
    {
        var core = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new MpvCore(pixelWidth, pixelHeight, headless, enableAudio, optionOverrides);
        }, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await core.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new LibMpvEngine(core, videoQualityResources);
    }

    public async ValueTask<long> LoadAsync(string url, LoadMode mode,
        IReadOnlyList<KeyValuePair<string, string>> fileOptions, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, MpvValue?>(StringComparer.Ordinal);
        foreach (var pair in fileOptions)
        {
            ArgumentException.ThrowIfNullOrEmpty(pair.Key);
            if (!options.TryAdd(pair.Key, new MpvValue.Text(pair.Value)))
                throw new ArgumentException("播放文件选项重复。", nameof(fileOptions));
        }
        var flags = mode switch
        {
            LoadMode.Replace => "replace",
            LoadMode.Append => "append",
            _ => throw new ArgumentException("播放加载模式无效。", nameof(mode)),
        };
        var entryId = await Core.LoadFileAsync(url, flags, options, cancellationToken).ConfigureAwait(false);
        if (entryId < 0) throw new InvalidOperationException("播放器未返回有效条目标识。");
        return entryId;
    }

    public async ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken) =>
        await Core.CommandAsync(arguments, cancellationToken).ConfigureAwait(false);

    public async ValueTask SetAsync(string propertyName, EngineValue value, CancellationToken cancellationToken) =>
        await Core.SetPropertyAsync(propertyName, ToNativeValue(value), cancellationToken).ConfigureAwait(false);

    public void SetCompositionSize(int width, int height) => Core.SetOutputSize(width, height);

    public ValueTask PrepareVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken) =>
        new(Task.Run(() => videoQuality.PrepareAsync(mode, cancellationToken), cancellationToken));

    public ValueTask ApplyVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken) =>
        new(Task.Run(() => videoQuality.ApplyAsync(mode, cancellationToken), cancellationToken));

    public void ApplyHdr(bool enabled, double peakLuminance = 1000, double minimumLuminance = 0,
        double referenceWhite = 203)
    {
        var peak = double.IsFinite(peakLuminance) && peakLuminance > 0 ? peakLuminance : 1000;
        var white = double.IsFinite(referenceWhite) && referenceWhite > 0 ? referenceWhite : 203;
        var contrast = double.IsFinite(minimumLuminance) && minimumLuminance > 0
            ? (peak / minimumLuminance).ToString(CultureInfo.InvariantCulture) : "inf";
        Core.SetProperty("target-colorspace-hint", enabled);
        Core.SetProperty("target-colorspace-hint-mode", "target");
        Core.SetProperty("target-trc", enabled ? "pq" : "auto");
        Core.SetProperty("target-prim", enabled ? "bt.2020" : "auto");
        Core.SetProperty("target-peak", enabled ? peak.ToString(CultureInfo.InvariantCulture) : "auto");
        Core.SetProperty("target-contrast", enabled ? contrast : "auto");
        Core.SetProperty("hdr-reference-white", enabled ? white.ToString(CultureInfo.InvariantCulture) : "auto");
    }

    private async Task ForwardEventsAsync()
    {
        try
        {
            await foreach (var message in Core.Messages.ReadAllAsync().ConfigureAwait(false))
            {
                if (message is MpvMessage.SwapChainChanged changed)
                {
                    Volatile.Write(ref currentSwapChain, changed.Reference);
                    try { SwapChainChanged?.Invoke(changed.Reference); }
                    catch { events.Writer.TryWrite(new EngineEvent.Failure("视频画面绑定失败。")); }
                    continue;
                }
                if (message is MpvMessage.PropertyChanged { Name: "video-params" } parameters)
                    UpdateVideoAspect(parameters.Value);
                if (ToEngineEvent(message) is { } copied) events.Writer.TryWrite(copied);
                // 溢出后的关键属性在后台重新读取，恢复状态而不让 UI 处理原始事件。
                if (message is MpvMessage.QueueOverflow)
                    foreach (var pair in Properties)
                    {
                        var value = Core.GetProperty(pair.Key);
                        if (pair.Key == "video-params") UpdateVideoAspect(value);
                        events.Writer.TryWrite(new EngineEvent.PropertyChanged(pair.Value, ToEngineValue(value)));
                    }
            }
        }
        catch (ObjectDisposedException) { }
        catch { events.Writer.TryWrite(new EngineEvent.Failure("播放器事件转发意外结束。")); }
        finally { events.Writer.TryComplete(); }
    }

    private void UpdateVideoAspect(MpvValue? parameters)
    {
        if (ParseVideoAspect(parameters) is not { } aspect || VideoAspect == aspect) return;
        Volatile.Write(ref videoAspect, aspect);
        try { VideoAspectChanged?.Invoke(aspect); }
        catch { events.Writer.TryWrite(new EngineEvent.Failure("视频画面布局失败。")); }
    }

    internal static double? ParseVideoAspect(MpvValue? parameters)
    {
        if (parameters is not MpvValue.Map map) return null;
        static double Number(MpvValue? value) => value switch
        {
            MpvValue.WholeNumber integer => integer.Value,
            MpvValue.Number number => number.Value,
            _ => double.NaN,
        };
        var width = Number(map.Values.GetValueOrDefault("dw"));
        var height = Number(map.Values.GetValueOrDefault("dh"));
        var rotation = map.Values.TryGetValue("rotate", out var rotate) ? Number(rotate) : 0;
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 || !double.IsFinite(rotation))
            return null;
        rotation = (rotation % 360 + 360) % 360;
        var aspect = rotation is 90 or 270 ? height / width : width / height;
        return double.IsFinite(aspect) && aspect > 0 ? aspect : null;
    }

    private static EngineEvent? ToEngineEvent(MpvMessage message) => message switch
    {
        MpvMessage.StartFile start => new EngineEvent.StartFile(start.EntryId),
        MpvMessage.EndFile end => new EngineEvent.EndFile(end.EntryId, (EngineEndReason)end.Reason, end.Error),
        MpvMessage.FileLoaded => new EngineEvent.FileLoaded(),
        MpvMessage.PlaybackRestart => new EngineEvent.PlaybackRestart(),
        MpvMessage.VideoReconfig => new EngineEvent.VideoReconfig(),
        MpvMessage.PropertyChanged changed when Properties.TryGetValue(changed.Name, out var id) =>
            new EngineEvent.PropertyChanged(id, ToEngineValue(changed.Value)),
        MpvMessage.QueueOverflow => new EngineEvent.QueueOverflow(),
        MpvMessage.Shutdown => new EngineEvent.Shutdown(),
        MpvMessage.Failure failure => new EngineEvent.Failure(failure.Text),
        _ => null,
    };

    private static readonly Dictionary<string, EngineProperty> Properties = new(StringComparer.Ordinal)
    {
        ["time-pos"] = EngineProperty.TimePosition, ["duration"] = EngineProperty.Duration,
        ["pause"] = EngineProperty.Pause, ["paused-for-cache"] = EngineProperty.PausedForCache,
        ["seeking"] = EngineProperty.Seeking, ["core-idle"] = EngineProperty.CoreIdle,
        ["idle-active"] = EngineProperty.IdleActive, ["eof-reached"] = EngineProperty.EofReached,
        ["speed"] = EngineProperty.Speed, ["volume"] = EngineProperty.Volume, ["mute"] = EngineProperty.Mute,
        ["aid"] = EngineProperty.AudioTrack, ["sid"] = EngineProperty.SubtitleTrack,
        ["track-list"] = EngineProperty.TrackList, ["video-params"] = EngineProperty.VideoParameters,
        ["video-target-params"] = EngineProperty.VideoTargetParameters, ["hwdec-current"] = EngineProperty.HardwareDecoder,
        ["demuxer-cache-state"] = EngineProperty.DemuxerCacheState, ["playlist-pos"] = EngineProperty.PlaylistPosition,
        ["playlist-count"] = EngineProperty.PlaylistCount, ["current-ao"] = EngineProperty.AudioOutput,
        ["sub-delay"] = EngineProperty.SubtitleDelay,
    };

    private static EngineValue? ToEngineValue(MpvValue? value) => value switch
    {
        MpvValue.Text text => new EngineValue.Text(text.Value),
        MpvValue.WholeNumber integer => new EngineValue.WholeNumber(integer.Value),
        MpvValue.Number number => new EngineValue.Number(number.Value),
        MpvValue.Flag flag => new EngineValue.Flag(flag.Value),
        MpvValue.Array array => new EngineValue.Array(array.Values.Select(ToEngineValue).ToArray()),
        MpvValue.Map map => new EngineValue.Map(map.Values.ToDictionary(pair => pair.Key, pair => ToEngineValue(pair.Value), StringComparer.Ordinal)),
        _ => null,
    };

    private static MpvValue ToNativeValue(EngineValue value) => value switch
    {
        EngineValue.Text text => new MpvValue.Text(text.Value),
        EngineValue.WholeNumber integer => new MpvValue.WholeNumber(integer.Value),
        EngineValue.Number number => new MpvValue.Number(number.Value),
        EngineValue.Flag flag => new MpvValue.Flag(flag.Value),
        EngineValue.Array array => new MpvValue.Array(array.Values.Select(item => item is null ? null : ToNativeValue(item)).ToArray()),
        EngineValue.Map map => new MpvValue.Map(map.Values.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : ToNativeValue(pair.Value), StringComparer.Ordinal)),
        _ => throw new ArgumentException("不支持的播放器参数类型。", nameof(value)),
    };

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        try
        {
            try { await videoQuality.DisposeAsync().ConfigureAwait(false); }
            finally { await Core.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            await forwarding.ConfigureAwait(false);
            Volatile.Write(ref currentSwapChain, null);
            SwapChainChanged = null;
            VideoAspectChanged = null;
        }
    }
}
