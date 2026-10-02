using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.Core.Playback;
using Mambo.Player.External;

namespace Mambo.App.Debug;

/// <summary>Debug / AOT 下验证真实 IPC 编解码；只启动本进程内的假管道服务。</summary>
internal static class ExternalIpcLabSmoke
{
    private static readonly string[] SeekCommand = ["seek", "7", "absolute"];
    public static async Task RunAsync(string reportPath)
    {
        var report = new ExternalIpcLabReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var pipeName = "mambo-p7-probe-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task? serving = null;
        ExternalMpvEngine? engine = null;
        try
        {
            var accepting = server.WaitForConnectionAsync(token);
            await stream.ConnectAsync(token);
            await accepting;
            serving = ServeAsync(server, report, token);
            report.Stage = "初始化 IPC";
            engine = await ExternalMpvEngine.CreateForTestingAsync(stream, expectedServerProcessId: Environment.ProcessId,
                cancellationToken: token);
            report.Initialized = report.ObservedProperties == 21;
            report.Stage = "加载文件与事件";
            var id = await engine.LoadAsync("https://" + Guid.NewGuid().ToString("N") + ".invalid/video",
                LoadMode.Replace, [new("force-media-title", "测试🎬"), new("http-header-fields", "")], token);
            report.NativeEntryId = id == 73;
            while (!report.TypedNodes || !report.Started || !report.Loaded)
            {
                var value = await engine.Events.ReadAsync(token);
                if (value is EngineEvent.StartFile { EntryId: 73 }) report.Started = true;
                if (value is EngineEvent.FileLoaded) report.Loaded = true;
                if (value is EngineEvent.PropertyChanged { Property: EngineProperty.TrackList,
                    Value: MpvValue.Array { Values.Count: 1 } tracks } &&
                    tracks.Values[0] is MpvValue.Map map &&
                    map.Values["title"] is MpvValue.Text { Value: "测试🎬" } &&
                    map.Values["id"] is MpvValue.WholeNumber { Value: 2 }) report.TypedNodes = true;
            }
            report.Stage = "类型化命令与关闭";
            await engine.SetAsync("pause", new MpvValue.Flag(true), token);
            await engine.CommandAsync(SeekCommand, token);
            await engine.DisposeAsync(); engine = null;
            await serving.WaitAsync(token);
            report.Passed = report.Initialized && report.NativeEntryId && report.Started && report.Loaded &&
                report.TypedNodes && report.FileOptions && report.Unicode && report.Paused && report.Seek && report.Quit;
            report.Stage = report.Passed ? "完成" : "协议检查未通过";
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            report.HResult = error.HResult.ToString("X8", CultureInfo.InvariantCulture);
        }
        finally
        {
            try { if (engine is not null) await engine.DisposeAsync(); }
            catch (Exception error) { report.Passed = false; report.ErrorKind = error.GetType().Name; }
            await deadline.CancelAsync();
            if (serving is not null)
                try { await serving; }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                catch (Exception error) { report.Passed = false; report.ErrorKind = error.GetType().Name; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ExternalIpcLabJsonContext.Default.ExternalIpcLabReport));
        }
    }

    private static async Task ServeAsync(Stream stream, ExternalIpcLabReport report, CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var command = root.GetProperty("command");
            var name = command[0].GetString();
            var data = "null";
            if (name == "observe_property") report.ObservedProperties++;
            if (name == "loadfile")
            {
                report.FileOptions = command.GetArrayLength() == 5 && command[2].GetString() == "replace" &&
                    command[3].GetInt64() == -1 && command[4].GetProperty("http-header-fields").GetString() == "";
                report.Unicode = command[4].GetProperty("force-media-title").GetString() == "测试🎬" &&
                    !line.Contains("\\uD83C", StringComparison.OrdinalIgnoreCase);
                data = "{\"playlist_entry_id\":73}";
            }
            if (name == "set_property" && command[1].GetString() == "pause") report.Paused = command[2].GetBoolean();
            if (name == "seek") report.Seek = command[1].GetString() == "7" && command[2].GetString() == "absolute";
            if (name == "quit") report.Quit = true;
            var id = root.GetProperty("request_id").GetInt64().ToString(CultureInfo.InvariantCulture);
            await writer.WriteLineAsync(("{\"request_id\":" + id + ",\"error\":\"success\",\"data\":" + data + "}").AsMemory(), token);
            if (name == "loadfile")
            {
                await writer.WriteLineAsync("{\"event\":\"start-file\",\"playlist_entry_id\":73}".AsMemory(), token);
                await writer.WriteLineAsync("{\"event\":\"file-loaded\"}".AsMemory(), token);
                await writer.WriteLineAsync("{\"event\":\"property-change\",\"name\":\"track-list\",\"data\":[{\"id\":2,\"title\":\"测试🎬\",\"selected\":true}]}".AsMemory(), token);
            }
            if (name == "quit") return;
        }
    }
}

internal sealed class ExternalIpcLabReport
{
    public bool Passed { get; set; }
    public bool Initialized { get; set; }
    public int ObservedProperties { get; set; }
    public bool NativeEntryId { get; set; }
    public bool Started { get; set; }
    public bool Loaded { get; set; }
    public bool TypedNodes { get; set; }
    public bool FileOptions { get; set; }
    public bool Unicode { get; set; }
    public bool Paused { get; set; }
    public bool Seek { get; set; }
    public bool Quit { get; set; }
    public string Stage { get; set; } = "连接假 IPC";
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExternalIpcLabReport))]
internal sealed partial class ExternalIpcLabJsonContext : JsonSerializerContext;
