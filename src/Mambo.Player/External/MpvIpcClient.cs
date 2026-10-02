using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Mambo.Core.Playback;

namespace Mambo.Player.External;

/// <summary>一个会话持有一条 duplex IPC 连接。消息只有类型化 JSON；不记录命令和远端原始错误。</summary>
public sealed class MpvIpcClient : IAsyncDisposable
{
    private const int MaximumDepth = 32;
    private readonly Stream stream;
    private readonly TimeSpan defaultTimeout;
    private readonly int maximumFrameBytes;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<MpvValue?>> pending = new();
    private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object stateGate = new();
    private readonly Task reading;
    private long nextRequestId;
    private bool terminated;
    private Task? disposal;
    private Task? resnapshot;
    private int resnapshotRunning;

    public ChannelReader<EngineEvent> Events => events.Reader;

    public MpvIpcClient(Stream duplexStream, TimeSpan? replyTimeout = null, int maximumFrameBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(duplexStream);
        if (!duplexStream.CanRead || !duplexStream.CanWrite)
            throw new ArgumentException("播放器 IPC 需要双向连接。", nameof(duplexStream));
        defaultTimeout = replyTimeout ?? TimeSpan.FromSeconds(3);
        if (defaultTimeout <= TimeSpan.Zero || defaultTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(replyTimeout));
        if (maximumFrameBytes is < 256 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        stream = duplexStream;
        this.maximumFrameBytes = maximumFrameBytes;
        lifetimeToken = lifetime.Token;
        reading = ReadFramesAsync();
    }

    public async Task<MpvValue?> RequestAsync(IReadOnlyList<MpvValue?> command,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0 || command[0] is not MpvValue.Text { Value.Length: > 0 })
            throw new ArgumentException("播放器命令不能为空。", nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = timeout ?? defaultTimeout;
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var requestId = Interlocked.Increment(ref nextRequestId);
        var frame = EncodeRequest(requestId, command);
        var completion = new TaskCompletionSource<MpvValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(terminated || disposal is not null, this);
            pending[requestId] = completion;
        }
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadlineSource.CancelAfter(deadline);
        var writing = false;
        try
        {
            await writeGate.WaitAsync(deadlineSource.Token).ConfigureAwait(false);
            try
            {
                deadlineSource.Token.ThrowIfCancellationRequested();
                writing = true;
                await stream.WriteAsync(frame, deadlineSource.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadlineSource.Token).ConfigureAwait(false);
                writing = false;
            }
            finally { writeGate.Release(); }
            return await completion.Task.WaitAsync(deadlineSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (writing)
        {
            // 无法确定取消的 Write 是否只写了一半；这条连接必须丢弃，不能让后续消息拼接到残帧。
            Terminate(failure: false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("外部播放器 IPC 写入超时。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (lifetime.IsCancellationRequested) throw new IOException("外部播放器连接已结束。");
            throw new TimeoutException("外部播放器 IPC 请求超时。");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Terminate(failure: false);
            throw new IOException("外部播放器连接已结束。");
        }
        finally { pending.TryRemove(requestId, out _); }
    }

    private byte[] EncodeRequest(long requestId, IReadOnlyList<MpvValue?> command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MaximumDepth }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("request_id", requestId);
            writer.WriteStartArray("command");
            foreach (var argument in command) WriteValue(writer, argument, 0);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > maximumFrameBytes) throw new ArgumentException("播放器 IPC 请求过大。", nameof(command));
        // mpv 不接受用于非 BMP 字符的 JSON surrogate pair，保留其它 JSON 转义并转换合法配对。
        var json = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        var normalized = NormalizeSurrogateEscapes(json);
        return System.Text.Encoding.UTF8.GetBytes(normalized + "\n");
    }

    private static string NormalizeSurrogateEscapes(string json)
    {
        var result = new System.Text.StringBuilder(json.Length);
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (character == '\\' && index + 1 < json.Length)
            {
                if (index + 11 < json.Length && json[index + 1] == 'u' && json[index + 6] == '\\' && json[index + 7] == 'u' &&
                    ushort.TryParse(json.AsSpan(index + 2, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var high) && char.IsHighSurrogate((char)high) &&
                    ushort.TryParse(json.AsSpan(index + 8, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var low) && char.IsLowSurrogate((char)low))
                {
                    result.Append((char)high).Append((char)low);
                    index += 11;
                    continue;
                }
                result.Append(character).Append(json[++index]);
                continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static void WriteValue(Utf8JsonWriter writer, MpvValue? value, int depth)
    {
        if (depth >= MaximumDepth) throw new ArgumentException("播放器参数嵌套过深。", nameof(value));
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case MpvValue.Text text: writer.WriteStringValue(text.Value); break;
            case MpvValue.WholeNumber integer: writer.WriteNumberValue(integer.Value); break;
            case MpvValue.Number number when double.IsFinite(number.Value): writer.WriteNumberValue(number.Value); break;
            case MpvValue.Flag flag: writer.WriteBooleanValue(flag.Value); break;
            case MpvValue.Array array:
                writer.WriteStartArray();
                foreach (var item in array.Values) WriteValue(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case MpvValue.Map map:
                writer.WriteStartObject();
                foreach (var pair in map.Values) { writer.WritePropertyName(pair.Key); WriteValue(writer, pair.Value, depth + 1); }
                writer.WriteEndObject();
                break;
            default: throw new ArgumentException("播放器参数类型无效。", nameof(value));
        }
    }

    private async Task ReadFramesAsync()
    {
        var bytes = new byte[8192];
        var frame = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(bytes, lifetimeToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (frame.WrittenCount > 0) Terminate(failure: true);
                    return;
                }
                for (var index = 0; index < count; index++)
                {
                    if (bytes[index] == (byte)'\n')
                    {
                        if (frame.WrittenCount > 0) DispatchFrame(frame.WrittenMemory);
                        frame.Clear();
                        if (lifetime.IsCancellationRequested) return;
                        continue;
                    }
                    if (frame.WrittenCount == maximumFrameBytes) throw new InvalidDataException();
                    frame.GetSpan(1)[0] = bytes[index];
                    frame.Advance(1);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        { Terminate(failure: true); }
        finally { Terminate(failure: false); }
    }

    private void DispatchFrame(ReadOnlyMemory<byte> frame)
    {
        using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (root.TryGetProperty("request_id", out var id) && id.TryGetInt64(out var requestId))
        {
            if (!pending.TryGetValue(requestId, out var completion)) return; // 取消/超时后的迟到回复。
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String || error.GetString() != "success")
                completion.TrySetException(new InvalidOperationException("外部播放器未能执行请求。"));
            else
            {
                // 先解析再移除；无效数据仍由 Terminate 完成所有等待者，避免只留下当前请求等超时。
                var result = root.TryGetProperty("data", out var data) ? ReadValue(data) : null;
                completion.TrySetResult(result);
            }
            pending.TryRemove(requestId, out _);
            return;
        }
        if (!root.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String) return;
        EngineEvent? value = eventName.GetString() switch
        {
            "start-file" when TryEntryId(root, out var entryId) => new EngineEvent.StartFile(entryId),
            "file-loaded" => new EngineEvent.FileLoaded(),
            "playback-restart" => new EngineEvent.PlaybackRestart(),
            "end-file" when TryEntryId(root, out var entryId) => new EngineEvent.EndFile(entryId, ReadEndReason(root),
                ReadEndReason(root) == EngineEndReason.Error ? -1 : 0),
            "video-reconfig" => new EngineEvent.VideoReconfig(),
            "queue-overflow" => new EngineEvent.QueueOverflow(),
            "property-change" => ReadProperty(root),
            _ => null,
        };
        if (eventName.GetString() == "shutdown") { Terminate(failure: false); return; }
        if (value is not null) events.Writer.TryWrite(value);
        if (value is EngineEvent.QueueOverflow && Interlocked.CompareExchange(ref resnapshotRunning, 1, 0) == 0)
            resnapshot = ResnapshotAsync();
    }

    private async Task ResnapshotAsync()
    {
        try
        {
            foreach (var pair in Properties)
            {
                var value = await RequestAsync([new MpvValue.Text("get_property"), new MpvValue.Text(pair.Key)],
                    cancellationToken: lifetimeToken).ConfigureAwait(false);
                events.Writer.TryWrite(new EngineEvent.PropertyChanged(pair.Value, value));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException or InvalidOperationException) { }
        finally { Volatile.Write(ref resnapshotRunning, 0); }
    }

    private static bool TryEntryId(JsonElement root, out long id)
    {
        id = -1;
        return root.TryGetProperty("playlist_entry_id", out var element) && element.TryGetInt64(out id) && id >= 0;
    }

    private static EngineEndReason ReadEndReason(JsonElement root) =>
        root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() switch
        {
            "eof" => EngineEndReason.Eof, "stop" => EngineEndReason.Stop, "quit" => EngineEndReason.Quit,
            "redirect" => EngineEndReason.Redirect, _ => EngineEndReason.Error,
        } : EngineEndReason.Error;

    private static EngineEvent.PropertyChanged? ReadProperty(JsonElement root)
    {
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !Properties.TryGetValue(name.GetString()!, out var property)) return null;
        return new EngineEvent.PropertyChanged(property, root.TryGetProperty("data", out var data) ? ReadValue(data) : null);
    }

    private static MpvValue? ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => new MpvValue.Text(element.GetString()!),
        JsonValueKind.True => new MpvValue.Flag(true),
        JsonValueKind.False => new MpvValue.Flag(false),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => new MpvValue.WholeNumber(integer),
        JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => new MpvValue.Number(number),
        JsonValueKind.Array => new MpvValue.Array(element.EnumerateArray().Select(ReadValue).ToArray()),
        JsonValueKind.Object => ReadMap(element),
        _ => throw new InvalidDataException(),
    };

    private static MpvValue.Map ReadMap(JsonElement element)
    {
        var values = new Dictionary<string, MpvValue?>(StringComparer.Ordinal);
        foreach (var pair in element.EnumerateObject()) values[pair.Name] = ReadValue(pair.Value);
        return new MpvValue.Map(values);
    }

    internal static IReadOnlyDictionary<string, EngineProperty> ObservedProperties => Properties;

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
    };

    internal void NotifyProcessExited() => Terminate(failure: false);

    private void Terminate(bool failure)
    {
        lock (stateGate)
        {
            if (terminated) return;
            terminated = true;
            if (failure) events.Writer.TryWrite(new EngineEvent.Failure("外部播放器 IPC 消息无效或连接中断。"));
            events.Writer.TryWrite(new EngineEvent.Shutdown());
            events.Writer.TryComplete();
            foreach (var pair in pending)
                if (pending.TryRemove(pair.Key, out var completion))
                    completion.TrySetException(new IOException("外部播放器连接已结束。"));
        }
        lifetime.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate) return new(disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        Terminate(failure: false);
        await stream.DisposeAsync().ConfigureAwait(false);
        await reading.ConfigureAwait(false);
        if (resnapshot is not null) await resnapshot.ConfigureAwait(false);
        // 等正在写出的调用退出后再销毁信号量；避免释放后 Write 的 finally 再 Release。
        await writeGate.WaitAsync().ConfigureAwait(false);
        writeGate.Release();
        writeGate.Dispose();
        lifetime.Dispose();
    }
}
