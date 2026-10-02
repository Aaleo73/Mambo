using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Microsoft.Win32.SafeHandles;

namespace Mambo.Player.External;

/// <summary>只拥有自己启动的 mpv 进程；所有播放数据经 IPC 传递。</summary>
public sealed partial class ExternalMpvEngine : IPlayerEngine
{
    private readonly MpvIpcClient client;
    private readonly Process? process;
    private readonly Task processExited;
    private readonly Task[] outputDrains;
    private readonly CancellationTokenSource processObservation = new();
    private readonly object disposeGate = new();
    private Task? disposal;

    public EngineKind Kind => EngineKind.External;
    public ChannelReader<EngineEvent> Events => client.Events;

    private ExternalMpvEngine(MpvIpcClient client, Process? process = null, Task[]? outputDrains = null)
    {
        this.client = client;
        this.process = process;
        this.outputDrains = outputDrains ?? [];
        processExited = process is null ? Task.CompletedTask : ObserveExitAsync(process);
    }

    public static async Task<ExternalMpvEngine> CreateAsync(ExternalMpvApproval approval,
        IExternalPlayerValidator validator, IReadOnlyDictionary<string, string>? optionOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(validator);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("外部播放器仅支持 Windows。");
        ValidateOverrides(optionOverrides);
        cancellationToken.ThrowIfCancellationRequested();
        Process? ownedProcess = null;
        NamedPipeClientStream? pipe = null;
        ExternalMpvEngine? engine = null;
        var drains = Array.Empty<Task>();
        try
        {
            // 在复核与启动之间持有只读共享句柄，并核对打开文件的最终路径。
            using (var executableLock = new FileStream(approval.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                MpvExecutableApproval.EnsureHandlePath(approval.Path, executableLock);
                if (!await validator.VerifyAsync(approval, cancellationToken).ConfigureAwait(false))
                    throw new AppException(new AppError(AppErrorKind.Player, ErrorCodes.ExternalFileChanged,
                        "外部播放器文件已变化，请重新选择并批准。", Retryable: false));
                cancellationToken.ThrowIfCancellationRequested();
                var pipeName = "mambo-" + Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(approval.Path)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(approval.Path)!,
                };
                foreach (var argument in new[]
                {
                    "--config=no", "--load-scripts=no", "--terminal=no", "--idle=yes",
                    "--force-window=immediate", "--input-ipc-server=\\\\.\\pipe\\" + pipeName,
                    "--input-terminal=no", "--msg-level=all=no", "--save-position-on-quit=no",
                    "--ytdl=no", "--input-default-bindings=no", "--input-vo-keyboard=no", "--media-controls=no", "--osc=no",
                }) start.ArgumentList.Add(argument);
                ownedProcess = new Process { StartInfo = start };
                MpvExecutableApproval.EnsureHandlePath(approval.Path, executableLock);
                if (!ownedProcess.Start()) throw new InvalidOperationException("无法启动外部播放器。");
                // 即便某个构建忽略 msg-level，也只丢弃输出；不保存或传播可能含 URL 的日志。
                drains = [DrainAsync(ownedProcess.StandardOutput), DrainAsync(ownedProcess.StandardError)];
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            }
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connection.CancelAfter(TimeSpan.FromSeconds(5));
                var connecting = pipe.ConnectAsync(connection.Token);
                var exited = ownedProcess.WaitForExitAsync(connection.Token);
                var winner = await Task.WhenAny(connecting, exited).ConfigureAwait(false);
                if (winner == exited && ownedProcess.HasExited)
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    try { await connecting.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    throw new IOException("外部播放器在 IPC 连接前已退出。");
                }
                await connecting.ConfigureAwait(false);
                await connection.CancelAsync().ConfigureAwait(false);
                try { await exited.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            EnsurePipeServer(pipe, ownedProcess.Id);
            engine = new ExternalMpvEngine(new MpvIpcClient(pipe), ownedProcess, drains);
            pipe = null;
            ownedProcess = null;
            await engine.InitializeAsync(optionOverrides, cancellationToken).ConfigureAwait(false);
            return engine;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupStartupAsync(engine, pipe, ownedProcess, drains).ConfigureAwait(false);
            throw;
        }
        catch (AppException)
        {
            await CleanupStartupAsync(engine, pipe, ownedProcess, drains).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CleanupStartupAsync(engine, pipe, ownedProcess, drains).ConfigureAwait(false);
            throw new InvalidOperationException("外部播放器启动或连接失败，请检查批准的 MPV 文件。");
        }
    }

    /// <summary>用于协议验收：连接已有的 duplex 流，不选择或启动任何可执行文件。</summary>
    public static async Task<ExternalMpvEngine> CreateForTestingAsync(Stream duplexStream,
        IReadOnlyDictionary<string, string>? optionOverrides = null, TimeSpan? replyTimeout = null,
        int? expectedServerProcessId = null, CancellationToken cancellationToken = default)
    {
        ValidateOverrides(optionOverrides);
        if (expectedServerProcessId is { } processId)
        {
            try { EnsurePipeServer(duplexStream, processId); }
            catch { await duplexStream.DisposeAsync().ConfigureAwait(false); throw; }
        }
        var engine = new ExternalMpvEngine(new MpvIpcClient(duplexStream, replyTimeout));
        try
        {
            await engine.InitializeAsync(optionOverrides, cancellationToken).ConfigureAwait(false);
            return engine;
        }
        catch { await engine.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void EnsurePipeServer(Stream duplexStream, int expectedProcessId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("命名管道身份检查仅支持 Windows。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProcessId);
        if (duplexStream is not NamedPipeClientStream pipe ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
            serverProcessId != checked((uint)expectedProcessId))
            throw new IOException("外部播放器 IPC 服务端身份不匹配。");
    }

    private async Task InitializeAsync(IReadOnlyDictionary<string, string>? options, CancellationToken token)
    {
        await client.RequestAsync([new MpvValue.Text("request_log_messages"), new MpvValue.Text("no")],
            cancellationToken: token).ConfigureAwait(false);
        var index = 0;
        foreach (var pair in MpvIpcClient.ObservedProperties)
            await client.RequestAsync([new MpvValue.Text("observe_property"), new MpvValue.WholeNumber(++index),
                new MpvValue.Text(pair.Key)], cancellationToken: token).ConfigureAwait(false);
        if (options is not null)
            foreach (var pair in options)
                await client.RequestAsync([new MpvValue.Text("set_property"), new MpvValue.Text(pair.Key),
                    new MpvValue.Text(pair.Value)], cancellationToken: token).ConfigureAwait(false);
    }

    private static void ValidateOverrides(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) return;
        foreach (var pair in options)
            if (!AllowedOverrides.Contains(pair.Key) || pair.Value is null || pair.Value.Contains('\0'))
                throw new ArgumentException("外部播放器启动选项无效。", nameof(options));
    }

    private static readonly HashSet<string> AllowedOverrides = new(StringComparer.Ordinal)
    {
        "hwdec", "volume", "mute", "speed", "target-colorspace-hint", "target-colorspace-hint-mode",
        "target-trc", "target-prim", "target-peak", "target-contrast", "hdr-reference-white",
    };

    public async ValueTask<long> LoadAsync(string url, LoadMode mode,
        IReadOnlyList<KeyValuePair<string, string>> fileOptions, CancellationToken cancellationToken)
    {
        ThrowIfDisposing();
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(fileOptions);
        var flags = mode switch
        {
            LoadMode.Replace => "replace", LoadMode.Append => "append",
            _ => throw new ArgumentException("播放加载模式无效。", nameof(mode)),
        };
        var options = new Dictionary<string, MpvValue?>(StringComparer.Ordinal);
        foreach (var pair in fileOptions)
        {
            ArgumentException.ThrowIfNullOrEmpty(pair.Key);
            if (pair.Value is null || !options.TryAdd(pair.Key, new MpvValue.Text(pair.Value)))
                throw new ArgumentException("播放文件选项重复或无效。", nameof(fileOptions));
        }
        var result = await client.RequestAsync([new MpvValue.Text("loadfile"), new MpvValue.Text(url),
            new MpvValue.Text(flags), new MpvValue.WholeNumber(-1), new MpvValue.Map(options)],
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (result is MpvValue.Map map && map.Values.TryGetValue("playlist_entry_id", out var entry) &&
            entry is MpvValue.WholeNumber { Value: >= 0 } id) return id.Value;
        throw new InvalidOperationException("外部播放器未返回有效条目标识。");
    }

    public async ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken)
    {
        ThrowIfDisposing();
        await client.RequestAsync(arguments.ToArray().Select(value => (MpvValue?)new MpvValue.Text(value)).ToArray(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string propertyName, MpvValue value, CancellationToken cancellationToken)
    {
        ThrowIfDisposing();
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ArgumentNullException.ThrowIfNull(value);
        await client.RequestAsync([new MpvValue.Text("set_property"), new MpvValue.Text(propertyName), value],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposing()
    {
        lock (disposeGate) ObjectDisposedException.ThrowIf(disposal is not null, this);
    }

    private async Task ObserveExitAsync(Process ownedProcess)
    {
        try { await ownedProcess.WaitForExitAsync(processObservation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (processObservation.IsCancellationRequested) { }
        finally { client.NotifyProcessExited(); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[512];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        try
        {
            try
            {
                await client.RequestAsync([new MpvValue.Text("quit")], TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException) { }
            if (process is not null) await StopOwnedProcessAsync(process).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await processObservation.CancelAsync().ConfigureAwait(false);
            try { await processExited.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            await AwaitDrainsAsync(outputDrains).ConfigureAwait(false);
            process?.Dispose();
            processObservation.Dispose();
        }
    }

    private static async Task StopOwnedProcessAsync(Process ownedProcess)
    {
        if (ownedProcess.HasExited) return;
        using (var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            try { await ownedProcess.WaitForExitAsync(grace.Token).ConfigureAwait(false); return; }
            catch (OperationCanceledException) { }
        try { if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { if (!ownedProcess.HasExited) ownedProcess.Kill(); }
            catch (System.ComponentModel.Win32Exception) { throw new IOException("无法结束本次外部播放器进程。"); }
            catch (InvalidOperationException) when (ownedProcess.HasExited) { }
        }
        catch (InvalidOperationException) when (ownedProcess.HasExited) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ownedProcess.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new IOException("外部播放器进程未能退出。"); }
    }

    private static async Task AwaitDrainsAsync(Task[] drains)
    {
        if (drains.Length == 0) return;
        try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task CleanupStartupAsync(ExternalMpvEngine? engine, NamedPipeClientStream? pipe,
        Process? process, Task[] drains)
    {
        if (engine is not null) { await engine.DisposeAsync().ConfigureAwait(false); return; }
        try { if (process is not null) await StopOwnedProcessAsync(process).ConfigureAwait(false); }
        finally
        {
            if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
            await AwaitDrainsAsync(drains).ConfigureAwait(false);
            process?.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
