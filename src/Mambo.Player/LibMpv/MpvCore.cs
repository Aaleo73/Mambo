using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Mambo.Player.LibMpv;

public sealed class MpvCore : IAsyncDisposable
{
    private readonly MpvHandle handle;
    private readonly object gate = new();
    private readonly Channel<MpvMessage> messages = Channel.CreateUnbounded<MpvMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<MpvValue?>> replies = new();
    private readonly TaskCompletionSource eventLoopEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? disposeTask;
    private long nextRequestId = 1000;
    private bool disposed;
    private volatile bool abortWait;
    private nint lastSwapChain;
    private readonly ConcurrentBag<MpvSwapChain> swapChainReferences = [];

    public ChannelReader<MpvMessage> Messages => messages.Reader;

    public MpvCore(int pixelWidth, int pixelHeight, bool headless = false, bool enableAudio = true,
        IReadOnlyDictionary<string, string>? optionOverrides = null)
    {
        var probe = MpvRuntime.Probe();
        if (!probe.Available) throw new InvalidOperationException(probe.Message);
        handle = new MpvHandle();
        if (handle.IsInvalid) throw new InvalidOperationException("无法创建内置播放器。");
        try
        {
            var options = new Dictionary<string, string>
            {
                ["config"] = "no", ["load-scripts"] = "no", ["ytdl"] = "no", ["terminal"] = "no",
                ["input-default-bindings"] = "no", ["input-vo-keyboard"] = "no", ["osc"] = "no",
                ["osd-level"] = "0", ["osd-bar"] = "no", ["cursor-autohide"] = "no",
                ["idle"] = "yes", ["keep-open"] = "no", ["force-window"] = headless ? "no" : "immediate",
                ["vo"] = headless ? "null" : "gpu-next",
                ["hwdec"] = headless ? "no" : "d3d11va",
                ["cache"] = "yes", ["demuxer-max-bytes"] = "128MiB", ["demuxer-max-back-bytes"] = "64MiB",
                ["demuxer-readahead-secs"] = "15", ["network-timeout"] = "15",
                ["user-agent"] = "Mambo/0.0.0", ["audio-client-name"] = "Mambo",
                ["media-controls"] = "no", ["sub-auto"] = "no", ["slang"] = "zh-CN,zh-Hans,chi,zho,chs,zh,eng,en",
            };
            // ao 是驱动列表，"auto" 不是音频驱动。正常播放保留 mpv 默认的设备选择；
            // 只有显式无声诊断才指定 null，否则视频可以起播却没有任何声音输出。
            if (headless || !enableAudio) options["ao"] = "null";
            if (!headless)
            {
                options["gpu-api"] = "d3d11";
                options["gpu-context"] = "d3d11";
                options["d3d11-output-mode"] = "composition";
                options["d3d11-composition-size"] = $"{Math.Max(1, pixelWidth)}x{Math.Max(1, pixelHeight)}";
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Mambo", "mpv", "shader-cache");
                Directory.CreateDirectory(cache);
                options["gpu-shader-cache-dir"] = cache;
                options["sub-fonts-dir"] = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
                options["sub-font"] = "MiSans";
            }
            if (optionOverrides is not null)
                foreach (var option in optionOverrides) options[option.Key] = option.Value;
            foreach (var option in options)
                Check(LibMpvNative.mpv_set_option_string(handle, option.Key, option.Value));
            Check(LibMpvNative.mpv_initialize(handle));
            // P0 不接收未经脱敏的原始 mpv 日志；诊断仅输出白名单属性和错误码。
            Check(LibMpvNative.mpv_request_log_messages(handle, "no"));
            Observe();
            new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" }.Start();
        }
        catch { handle.Dispose(); throw; }
    }

    private static void Check(int error)
    {
        if (error < 0)
            throw new InvalidOperationException($"播放器操作失败（代码 {error}）。");
    }

    private void Observe()
    {
        var properties = new (string Name, MpvFormat Format)[]
        {
            ("time-pos", MpvFormat.Double), ("duration", MpvFormat.Double), ("pause", MpvFormat.Flag),
            ("paused-for-cache", MpvFormat.Flag), ("seeking", MpvFormat.Flag), ("core-idle", MpvFormat.Flag),
            ("idle-active", MpvFormat.Flag), ("eof-reached", MpvFormat.Flag), ("speed", MpvFormat.Double),
            ("volume", MpvFormat.Double), ("mute", MpvFormat.Flag), ("aid", MpvFormat.String),
            ("sid", MpvFormat.String), ("track-list", MpvFormat.Node), ("video-params", MpvFormat.Node),
            ("video-target-params", MpvFormat.Node), ("hwdec-current", MpvFormat.String),
            ("demuxer-cache-state", MpvFormat.Node), ("playlist-pos", MpvFormat.Int64), ("playlist-count", MpvFormat.Int64),
            ("current-ao", MpvFormat.String),
        };
        for (var i = 0; i < properties.Length; i++)
            Check(LibMpvNative.mpv_observe_property(handle, (ulong)(i + 1), properties[i].Name, properties[i].Format));
    }

    public async Task<long> LoadFileAsync(string address, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var options = new Dictionary<string, MpvValue?>();
        if (headers is { Count: > 0 })
        {
            foreach (var pair in headers)
                if (pair.Key.IndexOfAny(['\r', '\n', ':', ',']) >= 0 || pair.Value.IndexOfAny(['\r', '\n', ',']) >= 0)
                    throw new ArgumentException("播放请求头包含无效字符。", nameof(headers));
            options["http-header-fields"] = new MpvValue.Text(string.Join(",", headers.Select(pair => $"{pair.Key}: {pair.Value}")));
        }
        return await LoadFileAsync(address, "replace", options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> LoadFileAsync(string address, string mode,
        IReadOnlyDictionary<string, MpvValue?> options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        if (mode is not ("replace" or "append")) throw new ArgumentException("播放加载模式无效。", nameof(mode));
        // 第三个参数是 index，第四个参数必须用 NODE_MAP，不能拼接转义的命令字符串。
        var result = await CommandNodeAsync(new MpvValue.Array(
            [new MpvValue.Text("loadfile"), new MpvValue.Text(address), new MpvValue.Text(mode),
             new MpvValue.WholeNumber(-1), new MpvValue.Map(options)]), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return result is MpvValue.Map map &&
            map.Values.GetValueOrDefault("playlist_entry_id") is MpvValue.WholeNumber entry ? entry.Value : -1;
    }

    public Task<MpvValue?> CommandAsync(params string[] arguments) => CommandAsync(arguments.AsMemory());

    public Task<MpvValue?> CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken = default) => CommandNodeAsync(
        new MpvValue.Array(arguments.ToArray().Select(x => (MpvValue?)new MpvValue.Text(x)).ToArray()),
        TimeSpan.FromSeconds(3), cancellationToken);

    private async Task<MpvValue?> CommandNodeAsync(MpvValue arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = (ulong)Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<MpvValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = completion;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                using var builder = new MpvNodeBuilder();
                var node = builder.Build(arguments);
                Check(LibMpvNative.mpv_command_node_async(handle, id, ref node));
            }
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { replies.TryRemove(id, out _); }
    }

    public async Task SetPropertyAsync(string name, MpvValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = (ulong)Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<MpvValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = completion;
        try
        {
            SetPropertyNodeAsync(id, name, value, cancellationToken);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        finally { replies.TryRemove(id, out _); }
    }

    private unsafe void SetPropertyNodeAsync(ulong id, string name, MpvValue value, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var builder = new MpvNodeBuilder();
            var node = builder.Build(value);
            Check(LibMpvNative.mpv_set_property_async(handle, id, name, MpvFormat.Node, &node));
        }
    }

    public unsafe void SetProperty(string name, string value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var data = Marshal.StringToCoTaskMemUTF8(value);
            try { Check(LibMpvNative.mpv_set_property_async(handle, 0, name, MpvFormat.String, &data)); }
            finally { Marshal.FreeCoTaskMem(data); }
        }
    }

    public void SetProperty(string name, double value) => SetProperty(name, value.ToString(CultureInfo.InvariantCulture));
    public void SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");
    public void SetOutputSize(int width, int height) => SetProperty("d3d11-composition-size", $"{Math.Max(1, width)}x{Math.Max(1, height)}");

    public unsafe MpvValue? GetProperty(string name)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            MpvNode node = default;
            var error = LibMpvNative.mpv_get_property(handle, name, MpvFormat.Node, &node);
            if (error < 0) return null;
            try { return MpvNodeReader.Read(node); }
            finally { LibMpvNative.mpv_free_node_contents(ref node); }
        }
    }

    private unsafe void ReadSwapChain()
    {
        long pointer = 0;
        if (LibMpvNative.mpv_get_property(handle, "display-swapchain", MpvFormat.Int64, &pointer) < 0) return;
        // 属性 getter 是 vo_get_display_swapchain 的借用指针；事件显式持有自己的引用。
        var reference = new MpvSwapChain((nint)pointer);
        if ((nint)pointer == lastSwapChain) { reference.Dispose(); return; }
        lastSwapChain = (nint)pointer;
        swapChainReferences.Add(reference);
        if (!messages.Writer.TryWrite(new MpvMessage.SwapChainChanged(reference))) reference.Dispose();
    }

    private unsafe void EventLoop()
    {
        try
        {
            ReadSwapChain();
            while (!abortWait)
            {
                var current = *(MpvEvent*)LibMpvNative.mpv_wait_event(handle, -1);
                MpvMessage? message = current.Id switch
                {
                    MpvEventId.Shutdown => new MpvMessage.Shutdown(),
                    MpvEventId.StartFile => new MpvMessage.StartFile(*(long*)current.Data),
                    MpvEventId.EndFile => CopyEndFile(current.Data),
                    MpvEventId.FileLoaded => new MpvMessage.FileLoaded(),
                    MpvEventId.PlaybackRestart => new MpvMessage.PlaybackRestart(),
                    MpvEventId.VideoReconfig => new MpvMessage.VideoReconfig(),
                    MpvEventId.PropertyChange => CopyProperty(current.Data),
                    MpvEventId.QueueOverflow => new MpvMessage.QueueOverflow(),
                    _ => null,
                };
                if (current.Id is MpvEventId.CommandReply or MpvEventId.SetPropertyReply &&
                    replies.TryRemove(current.ReplyUserData, out var completion))
                {
                    if (current.Error < 0)
                        completion.TrySetException(new InvalidOperationException($"播放器命令失败（代码 {current.Error}）。"));
                    else
                        completion.TrySetResult(current.Id == MpvEventId.CommandReply && current.Data != 0
                            ? MpvNodeReader.Read(*(MpvNode*)current.Data) : null);
                }
                if (current.Id == MpvEventId.SetPropertyReply && current.ReplyUserData == 0 && current.Error < 0)
                    messages.Writer.TryWrite(new MpvMessage.Failure($"播放器属性设置失败（代码 {current.Error}）。"));
                if (message is not null) messages.Writer.TryWrite(message);
                if (current.Id is MpvEventId.VideoReconfig or MpvEventId.FileLoaded) ReadSwapChain();
                if (current.Id == MpvEventId.Shutdown) break;
            }
        }
        catch { messages.Writer.TryWrite(new MpvMessage.Failure("播放器事件线程意外结束。")); }
        finally
        {
            foreach (var completion in replies.Values)
                completion.TrySetException(new ObjectDisposedException(nameof(MpvCore), "播放器已关闭。"));
            messages.Writer.TryComplete();
            eventLoopEnded.TrySetResult();
        }
    }

    private static unsafe MpvMessage.EndFile CopyEndFile(nint data)
    {
        var end = *(MpvEventEndFile*)data;
        return new MpvMessage.EndFile(end.PlaylistEntryId, end.Reason, end.Error);
    }
    private static unsafe MpvMessage.PropertyChanged CopyProperty(nint data)
    {
        var property = *(MpvEventProperty*)data;
        return new MpvMessage.PropertyChanged(Marshal.PtrToStringUTF8(property.Name) ?? "",
            property.Data == 0 ? null : MpvNodeReader.ReadProperty(property));
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposeTask is not null) return new(disposeTask);
            disposed = true;
            using var builder = new MpvNodeBuilder();
            var node = builder.Build(new MpvValue.Array([new MpvValue.Text("quit")]));
            LibMpvNative.mpv_command_node_async(handle, 0, ref node);
            disposeTask = CloseAsync();
            return new(disposeTask);
        }
    }

    private async Task CloseAsync()
    {
        try { await eventLoopEnded.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            abortWait = true;
            LibMpvNative.mpv_wakeup(handle);
            try { await eventLoopEnded.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                handle.SetHandleAsInvalid(); // 尚有线程调用 native，宁可泄漏也不并发 destroy。
                throw new TimeoutException("播放器事件线程未能退出，已放弃释放句柄。");
            }
        }
        var destroy = Task.Run(handle.Dispose);
        try { await destroy.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("播放器释放超时。"); }
        // 没有订阅者或尚未消费的事件也不能遗留 COM 引用。UI 必须在 DisposeAsync 前解绑。
        foreach (var reference in swapChainReferences) reference.Dispose();
    }
}
