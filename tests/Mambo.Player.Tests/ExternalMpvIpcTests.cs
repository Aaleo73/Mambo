using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Player.External;
using Xunit;

namespace Mambo.Player.Tests;

public sealed class ExternalMpvIpcTests
{
    private static readonly string[] StopCommand = ["stop"];
    [Fact]
    public async Task RepliesMatchRequestIdsEvenWhenReturnedInReverseOrder()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        server.Handler = request => Task.FromResult(request.GetProperty("command")[0].GetString() == "get_property");
        var first = client.RequestAsync([new MpvValue.Text("get_property"), new MpvValue.Text("volume")], cancellationToken: TestContext.Current.CancellationToken);
        var second = client.RequestAsync([new MpvValue.Text("get_property"), new MpvValue.Text("speed")], cancellationToken: TestContext.Current.CancellationToken);
        var requestOne = await server.ReadRequestAsync("get_property", TestContext.Current.CancellationToken);
        var requestTwo = await server.ReadRequestAsync("get_property", TestContext.Current.CancellationToken);
        Assert.NotEqual(requestOne.GetProperty("request_id").GetInt64(), requestTwo.GetProperty("request_id").GetInt64());
        await server.ReplyAsync(requestTwo, "1.5");
        await server.ReplyAsync(requestOne, "42");
        Assert.Equal(42, Assert.IsType<MpvValue.WholeNumber>(await first).Value);
        Assert.Equal(1.5, Assert.IsType<MpvValue.Number>(await second).Value);
    }

    [Fact]
    public async Task ReplyTimeoutAndLateReplyDoNotAffectNextRequest()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client, TimeSpan.FromMilliseconds(100));
        server.Handler = _ => Task.FromResult(true);
        var timedOut = client.RequestAsync([new MpvValue.Text("get_property"), new MpvValue.Text("volume")], cancellationToken: TestContext.Current.CancellationToken);
        var expiredRequest = await server.ReadRequestAsync("get_property", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
        await server.ReplyAsync(expiredRequest, "99");
        server.Handler = null;
        Assert.Null(await client.RequestAsync([new MpvValue.Text("set_property"), new MpvValue.Text("mute"), new MpvValue.Flag(true)],
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationBeforeDispatchSendsNoFrameAndPendingCancellationIgnoresLateReply()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync([new MpvValue.Text("stop")],
            cancellationToken: cancelled.Token));
        Assert.Equal(0, server.RequestCount);
        server.Handler = _ => Task.FromResult(true);
        using var pendingCancellation = new CancellationTokenSource();
        var pending = client.RequestAsync([new MpvValue.Text("stop")], cancellationToken: pendingCancellation.Token);
        var request = await server.ReadRequestAsync("stop", TestContext.Current.CancellationToken);
        pendingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await server.ReplyAsync(request);
        server.Handler = null;
        Assert.Null(await client.RequestAsync([new MpvValue.Text("set_property"), new MpvValue.Text("pause"), new MpvValue.Flag(true)], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EofFailsPendingRequestsAndEmitsShutdownOnce()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        var client = new MpvIpcClient(server.Client);
        server.Handler = _ => Task.FromResult(true);
        var pending = client.RequestAsync([new MpvValue.Text("get_property"), new MpvValue.Text("time-pos")], cancellationToken: TestContext.Current.CancellationToken);
        await server.ReadRequestAsync("get_property", TestContext.Current.CancellationToken);
        await server.DisconnectAsync();
        await Assert.ThrowsAsync<IOException>(() => pending);
        await client.DisposeAsync();
        var events = await ReadRemainingAsync(client.Events);
        Assert.Single(events.OfType<EngineEvent.Shutdown>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RequestAsync([new MpvValue.Text("stop")], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LifecycleAndStructuredPropertiesMatchEmbeddedEngineEvents()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(EngineKind.External, engine.Kind);
        var observed = server.ObservedProperties;
        Assert.Equal(21, observed.Count);
        Assert.Contains("time-pos", observed);
        Assert.Contains("demuxer-cache-state", observed);
        await server.SendAsync("{\"event\":\"start-file\",\"playlist_entry_id\":101}");
        await server.SendAsync("{\"event\":\"file-loaded\"}");
        await server.SendAsync("{\"event\":\"playback-restart\"}");
        await server.SendAsync("{\"event\":\"property-change\",\"name\":\"time-pos\",\"data\":12.5}");
        await server.SendAsync("{\"event\":\"property-change\",\"name\":\"pause\",\"data\":true}");
        await server.SendAsync("{\"event\":\"property-change\",\"name\":\"track-list\",\"data\":[{\"id\":2,\"type\":\"sub\",\"selected\":true,\"title\":\"简体中文\",\"extra\":null}]}");
        await server.SendAsync("{\"event\":\"property-change\",\"name\":\"duration\"}");
        await server.SendAsync("{\"event\":\"end-file\",\"playlist_entry_id\":101,\"reason\":\"eof\"}");
        await server.SendAsync("{\"event\":\"shutdown\"}");
        var values = await ReadRemainingAsync(engine.Events);
        Assert.Equal(101, Assert.IsType<EngineEvent.StartFile>(values[0]).EntryId);
        Assert.IsType<EngineEvent.FileLoaded>(values[1]);
        Assert.IsType<EngineEvent.PlaybackRestart>(values[2]);
        Assert.Equal(12.5, Assert.IsType<MpvValue.Number>(Assert.IsType<EngineEvent.PropertyChanged>(values[3]).Value).Value);
        Assert.True(Assert.IsType<MpvValue.Flag>(Assert.IsType<EngineEvent.PropertyChanged>(values[4]).Value).Value);
        var track = Assert.IsType<MpvValue.Map>(Assert.IsType<MpvValue.Array>(Assert.IsType<EngineEvent.PropertyChanged>(values[5]).Value).Values[0]);
        Assert.Equal(2, Assert.IsType<MpvValue.WholeNumber>(track.Values["id"]).Value);
        Assert.Equal("简体中文", Assert.IsType<MpvValue.Text>(track.Values["title"]).Value);
        Assert.Null(track.Values["extra"]);
        Assert.Null(Assert.IsType<EngineEvent.PropertyChanged>(values[6]).Value);
        var ended = Assert.IsType<EngineEvent.EndFile>(values[7]);
        Assert.Equal(101, ended.EntryId);
        Assert.Equal(EngineEndReason.Eof, ended.Reason);
        Assert.Equal(0, ended.Error);
        Assert.Single(values.OfType<EngineEvent.Shutdown>());
    }

    [Theory]
    [InlineData("stop", EngineEndReason.Stop, 0)]
    [InlineData("quit", EngineEndReason.Quit, 0)]
    [InlineData("redirect", EngineEndReason.Redirect, 0)]
    [InlineData("error", EngineEndReason.Error, -1)]
    public async Task EndFileReasonIsTypedWithoutPropagatingRawError(string reason, EngineEndReason expected, int error)
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        await server.SendAsync("{\"event\":\"end-file\",\"playlist_entry_id\":5,\"reason\":\"" + reason + "\",\"file_error\":\"raw private diagnostic\"}");
        await server.SendAsync("{\"event\":\"shutdown\"}");
        var values = await ReadRemainingAsync(client.Events);
        var ended = Assert.IsType<EngineEvent.EndFile>(values[0]);
        Assert.Equal(expected, ended.Reason);
        Assert.Equal(error, ended.Error);
        Assert.DoesNotContain("raw private diagnostic", string.Join(',', values));
    }

    [Fact]
    public async Task LoadUsesPerFileMapAndReturnsNativeIdsForReplaceAndAppend()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client,
            cancellationToken: TestContext.Current.CancellationToken);
        var first = await engine.LoadAsync("av://lavfi:testsrc=size=32x32", LoadMode.Replace,
            [new("start", "12.500"), new("http-header-fields", ""), new("force-media-title", "剧集😀")], TestContext.Current.CancellationToken);
        var request = await server.ReadRequestAsync("loadfile", TestContext.Current.CancellationToken);
        var command = request.GetProperty("command");
        Assert.Equal("replace", command[2].GetString());
        Assert.Equal(-1, command[3].GetInt64());
        Assert.Equal("12.500", command[4].GetProperty("start").GetString());
        Assert.Equal("", command[4].GetProperty("http-header-fields").GetString());
        Assert.Equal("剧集😀", command[4].GetProperty("force-media-title").GetString());
        Assert.Contains("剧集😀", server.LastFrame);
        Assert.DoesNotContain("\\uD83D", server.LastFrame, StringComparison.OrdinalIgnoreCase);
        var second = await engine.LoadAsync("av://lavfi:testsrc=size=48x48", LoadMode.Append,
            [new("http-header-fields", "")], TestContext.Current.CancellationToken);
        Assert.Equal(101, first);
        Assert.Equal(102, second);
        var append = await server.ReadRequestAsync("loadfile", TestContext.Current.CancellationToken);
        Assert.Equal("append", append.GetProperty("command")[2].GetString());
        await Assert.ThrowsAsync<ArgumentException>(async () => await engine.LoadAsync("av://lavfi:testsrc", LoadMode.Replace,
            [new("start", "1"), new("start", "2")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUsesNativeTypesAndRemoteErrorsUseSafeChineseText()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client,
            new Dictionary<string, string> { ["hwdec"] = "auto-safe" }, cancellationToken: TestContext.Current.CancellationToken);
        var hwdec = await server.ReadRequestAsync("set_property", TestContext.Current.CancellationToken);
        Assert.Equal("hwdec", hwdec.GetProperty("command")[1].GetString());
        await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken);
        var paused = await server.ReadRequestAsync("set_property", TestContext.Current.CancellationToken);
        Assert.True(paused.GetProperty("command")[2].GetBoolean());
        await engine.SetAsync("speed", new MpvValue.Number(1.5), TestContext.Current.CancellationToken);
        var speed = await server.ReadRequestAsync("set_property", TestContext.Current.CancellationToken);
        Assert.Equal(1.5, speed.GetProperty("command")[2].GetDouble());
        server.Handler = async request => { await server.ReplyAsync(request, error: "private error payload"); return true; };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.CommandAsync(StopCommand, TestContext.Current.CancellationToken));
        Assert.Equal("外部播放器未能执行请求。", error.Message);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task SuccessfulLoadReplyStillRequiresValidNativeEntryId()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client,
            cancellationToken: TestContext.Current.CancellationToken);
        server.Handler = async request =>
        {
            if (request.GetProperty("command")[0].GetString() != "loadfile") return false;
            await server.ReplyAsync(request, "{\"playlist_entry_id\":-1}");
            return true;
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.LoadAsync("av://lavfi:testsrc=size=32x32", LoadMode.Replace, [], TestContext.Current.CancellationToken));
        Assert.Equal("外部播放器未返回有效条目标识。", error.Message);
        await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("not-json\n")]
    [InlineData("{\"event\":\"property-change\",\"name\":\"pause\",\"data\":\n")]
    [InlineData("{\"event\":\"shutdown\"}")]
    public async Task InvalidOrTruncatedFrameCompletesWithSafeFailure(string raw)
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        await server.SendRawAsync(Encoding.UTF8.GetBytes(raw));
        await server.DisconnectAsync();
        var values = await ReadRemainingAsync(client.Events);
        Assert.Single(values.OfType<EngineEvent.Shutdown>());
        Assert.Single(values.OfType<EngineEvent.Failure>());
    }

    [Fact]
    public async Task FrameSizeLimitStopsUnboundedInputAndFragmentedUtf8IsReassembled()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client, maximumFrameBytes: 256);
        var bytes = Encoding.UTF8.GetBytes("{\"event\":\"property-change\",\"name\":\"hwdec-current\",\"data\":\"解码😀\"}\n");
        foreach (var value in bytes) await server.SendRawAsync(new[] { value });
        var property = Assert.IsType<EngineEvent.PropertyChanged>(await client.Events.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("解码😀", Assert.IsType<MpvValue.Text>(property.Value).Value);
        await server.SendRawAsync(Encoding.UTF8.GetBytes(new string('x', 257)));
        var values = await ReadRemainingAsync(client.Events);
        Assert.Single(values.OfType<EngineEvent.Failure>());
        Assert.Single(values.OfType<EngineEvent.Shutdown>());
    }

    [Fact]
    public async Task InvalidUtf8PropertyValueEndsConnectionWithoutRawException()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        var prefix = Encoding.UTF8.GetBytes("{\"event\":\"property-change\",\"name\":\"hwdec-current\",\"data\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}\n");
        await server.SendRawAsync([.. prefix, 0xff, .. suffix]);
        var values = await ReadRemainingAsync(client.Events);
        Assert.Single(values.OfType<EngineEvent.Failure>());
        Assert.Single(values.OfType<EngineEvent.Shutdown>());
    }

    [Fact]
    public async Task EscapedLiteralSurrogateTextAndNonBmpTextBothRemainUnchanged()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        const string text = "\\uD83D\\uDE00 与 😀 和 \"引号\"\n换行";
        await client.RequestAsync([new MpvValue.Text("set_property"), new MpvValue.Text("title"), new MpvValue.Text(text)], cancellationToken: TestContext.Current.CancellationToken);
        var request = await server.ReadRequestAsync("set_property", TestContext.Current.CancellationToken);
        Assert.Equal(text, request.GetProperty("command")[2].GetString());
        Assert.Contains("😀", server.LastFrame);
        Assert.DoesNotContain('\n', server.LastFrame);
    }

    [Fact]
    public async Task DisposeSendsQuitOnceCompletesEventsAndRejectsFurtherCalls()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client, cancellationToken: TestContext.Current.CancellationToken);
        var one = engine.DisposeAsync().AsTask();
        var two = engine.DisposeAsync().AsTask();
        await Task.WhenAll(one, two);
        var quit = await server.ReadRequestAsync("quit", TestContext.Current.CancellationToken);
        Assert.Equal("quit", quit.GetProperty("command")[0].GetString());
        Assert.Equal(1, server.QuitCount);
        Assert.Single((await ReadRemainingAsync(engine.Events)).OfType<EngineEvent.Shutdown>());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueOverflowResnapshotsPropertiesWithoutInterruptingPlayback()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = new MpvIpcClient(server.Client);
        server.Handler = async request =>
        {
            if (request.GetProperty("command")[0].GetString() != "get_property") return false;
            var name = request.GetProperty("command")[1].GetString();
            await server.ReplyAsync(request, name == "time-pos" ? "31.5" : name == "speed" ? "1.5" : "null");
            return true;
        };
        await server.SendAsync("{\"event\":\"queue-overflow\"}");
        Assert.IsType<EngineEvent.QueueOverflow>(await client.Events.ReadAsync(TestContext.Current.CancellationToken));
        EngineEvent.PropertyChanged? position = null;
        EngineEvent.PropertyChanged? rate = null;
        for (var index = 0; index < 21; index++)
        {
            var value = Assert.IsType<EngineEvent.PropertyChanged>(await client.Events.ReadAsync(TestContext.Current.CancellationToken));
            if (value.Property == EngineProperty.TimePosition) position = value;
            if (value.Property == EngineProperty.Speed) rate = value;
        }
        Assert.Equal(31.5, Assert.IsType<MpvValue.Number>(position!.Value).Value);
        Assert.Equal(1.5, Assert.IsType<MpvValue.Number>(rate!.Value).Value);
    }

    [Fact]
    public async Task DisposeRemainsBoundedWhenQuitGetsNoReply()
    {
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client, cancellationToken: TestContext.Current.CancellationToken);
        server.Handler = request => Task.FromResult(request.GetProperty("command")[0].GetString() == "quit");
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, server.QuitCount);
        Assert.Single((await ReadRemainingAsync(engine.Events)).OfType<EngineEvent.Shutdown>());
    }

    [Fact]
    public async Task FailedReadOnlyApprovalCheckNeverStartsExecutable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "外部播放器启动前文件锁检查仅适用于 Windows。");
        var directory = Path.Combine(Path.GetTempPath(), "mambo-approval-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mpv.exe");
        try
        {
            await File.WriteAllTextAsync(path, "not an executable", TestContext.Current.CancellationToken);
            var validator = new RejectingValidator();
            var approval = new ExternalMpvApproval(path, new string('0', 64), 17,
                File.GetLastWriteTimeUtc(path).Ticks, "0.40.0");
            var error = await Assert.ThrowsAsync<AppException>(() => ExternalMpvEngine.CreateAsync(approval, validator,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCodes.ExternalFileChanged, error.Error.Code);
            Assert.Equal(1, validator.VerifyCount);
            Assert.DoesNotContain(path, error.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NamedPipeServerProcessIdUsesRealWindowsAbi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "命名管道 PID 校验仅适用于 Windows。");
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        await using var engine = await ExternalMpvEngine.CreateForTestingAsync(server.Client,
            expectedServerProcessId: Environment.ProcessId, cancellationToken: TestContext.Current.CancellationToken);
        await engine.SetAsync("pause", new MpvValue.Flag(true), TestContext.Current.CancellationToken);
        Assert.Equal(21, server.ObservedProperties.Count);
    }

    [Fact]
    public async Task NamedPipeServerWithWrongProcessIdIsRejectedBeforeAnyIpcCommand()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "命名管道 PID 校验仅适用于 Windows。");
        await using var server = await FakeMpvIpcServer.CreateAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<IOException>(() => ExternalMpvEngine.CreateForTestingAsync(server.Client,
            expectedServerProcessId: Environment.ProcessId + 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("外部播放器 IPC 服务端身份不匹配。", error.Message);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task CancellationDuringPartialWriteDiscardsConnection()
    {
        var stream = new InterruptedWriteStream();
        await using var client = new MpvIpcClient(stream);
        using var cancelled = new CancellationTokenSource();
        var request = client.RequestAsync([new MpvValue.Text("stop")], cancellationToken: cancelled.Token);
        await stream.WritingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Single((await ReadRemainingAsync(client.Events)).OfType<EngineEvent.Shutdown>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RequestAsync([new MpvValue.Text("stop")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, stream.WriteCount);
    }

    private sealed class RejectingValidator : IExternalPlayerValidator
    {
        public int VerifyCount { get; private set; }
        public Task<ExternalMpvApproval> ValidateAsync(string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("测试不执行版本检查。");
        public Task<bool> VerifyAsync(ExternalMpvApproval approval, CancellationToken cancellationToken = default)
        { VerifyCount++; return Task.FromResult(false); }
    }

    private sealed class InterruptedWriteStream : Stream
    {
        public TaskCompletionSource WritingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            WritingStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task<List<EngineEvent>> ReadRemainingAsync(ChannelReader<EngineEvent> events)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var values = new List<EngineEvent>();
        await foreach (var value in events.ReadAllAsync(timeout.Token)) values.Add(value);
        return values;
    }
}

/// <summary>共享 P7 测试服务器：只有本机随机命名管道，不启动任何可执行文件。</summary>
internal sealed class FakeMpvIpcServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly NamedPipeClientStream client;
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<JsonElement> requests = Channel.CreateUnbounded<JsonElement>();
    private readonly List<string> observed = [];
    private readonly Task reading;
    private int requestCount;
    private int quitCount;
    private long nextEntryId = 100;
    private string lastFrame = "";
    private bool disconnected;

    public Stream Client => client;
    public Func<JsonElement, Task<bool>>? Handler { get; set; }
    public int RequestCount => Volatile.Read(ref requestCount);
    public int QuitCount => Volatile.Read(ref quitCount);
    public string LastFrame => Volatile.Read(ref lastFrame);
    public IReadOnlyList<string> ObservedProperties { get { lock (observed) return observed.ToArray(); } }

    private FakeMpvIpcServer(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        this.server = server;
        this.client = client;
        reading = ReadAsync();
    }

    public static async Task<FakeMpvIpcServer> CreateAsync(CancellationToken token = default)
    {
        var name = "mambo-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await Task.WhenAll(server.WaitForConnectionAsync(token), client.ConnectAsync(token));
            return new FakeMpvIpcServer(server, client);
        }
        catch { await server.DisposeAsync(); await client.DisposeAsync(); throw; }
    }

    private async Task ReadAsync()
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false, true), leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                Volatile.Write(ref lastFrame, line);
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement.Clone();
                Interlocked.Increment(ref requestCount);
                var command = request.GetProperty("command");
                var name = command[0].GetString();
                if (name == "observe_property") lock (observed) observed.Add(command[2].GetString()!);
                if (name == "quit") Interlocked.Increment(ref quitCount);
                requests.Writer.TryWrite(request);
                if (Handler is { } handler && await handler(request)) continue;
                var data = name == "loadfile" ? "{\"playlist_entry_id\":" + Interlocked.Increment(ref nextEntryId).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : "null";
                await ReplyAsync(request, data);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { requests.Writer.TryComplete(); }
    }

    public async Task<JsonElement> ReadRequestAsync(string commandName, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (var request in requests.Reader.ReadAllAsync(deadline.Token))
            if (request.GetProperty("command")[0].GetString() == commandName) return request;
        throw new IOException("测试 IPC 流已结束。");
    }

    public Task ReplyAsync(JsonElement request, string dataJson = "null", string error = "success") =>
        SendAsync("{\"request_id\":" + request.GetProperty("request_id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"error\":\"" + error + "\",\"data\":" + dataJson + "}");

    public Task SendAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json + "\n"));

    public async Task SendRawAsync(byte[] bytes)
    {
        await sending.WaitAsync(lifetime.Token);
        try { await server.WriteAsync(bytes, lifetime.Token); await server.FlushAsync(lifetime.Token); }
        finally { sending.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (disconnected) return;
        disconnected = true;
        await lifetime.CancelAsync();
        await server.DisposeAsync();
        await reading;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await client.DisposeAsync();
        lifetime.Dispose();
        sending.Dispose();
    }
}
