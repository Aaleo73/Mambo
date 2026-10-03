using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Mambo.App.Debug;

/// <summary>
/// 用短寿命 UIA 客户端检查假页面。协议只传控件标识和边界，不传文本输入值，
/// 不创建 App、单实例注册或窗口，也不把客户端 COM 缓存留在被测进程中。
/// </summary>
internal static partial class NativeUiaProbe
{
    internal const string Argument = "--native-uia-probe";
    private const int ProtocolVersion = 1;
    private const int MaximumMessageBytes = 16 * 1024;
    // 查询九秒，收束 owned child 最多一秒；await 不阻塞被测窗口的消息泵。
    private static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(9);

    internal static async Task<NativeUiaResult> QueryAsync(NativeUiaRequest request)
    {
        Process? child = null;
        var childIdentityMatched = false;
        long childStarted = 0;
        var executable = Environment.ProcessPath;
        var result = new NativeUiaResult("NativeUiaHelperFailed", []);
        using var budget = new CancellationTokenSource(QueryBudget);
        try
        {
            if (string.IsNullOrEmpty(executable)) return new("NativeUiaHelperExecutableMissing", []);
            using var parent = Process.GetCurrentProcess();
            request.Version = ProtocolVersion;
            request.Nonce = Guid.NewGuid().ToString("N");
            request.ParentProcessId = parent.Id;
            request.ParentStartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
            request.FakeScopeVerified = true; // caller has verified all six fake services before reading any Name
            if (!ValidRequest(request) || !WindowOwnedBy((nint)request.WindowHandle, parent.Id))
                return new("NativeUiaWindowScopeMismatch", []);

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            start.ArgumentList.Add(Argument);
            child = Process.Start(start);
            if (child is null) return new("NativeUiaHelperStartFailed", []);
            // Acquire and retain its kernel handle before sending any request. The child waits on stdin.
            _ = child.Handle;
            childStarted = child.StartTime.ToUniversalTime().Ticks;
            childIdentityMatched = MatchesProcess(child, childStarted, executable)
                && childStarted >= request.ParentStartedUtcTicks && ParentProcessId(child) == parent.Id;
            if (!childIdentityMatched) return new("NativeUiaHelperIdentityMismatch", []);

            // Drain diagnostic stderr without preserving or reporting it; the protocol is stdout only.
            var discardErrors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, budget.Token);
            ObserveFailure(discardErrors);
            await WriteMessageAsync(child.StandardInput.BaseStream, request,
                NativeUiaJsonContext.Default.NativeUiaRequest, budget.Token).ConfigureAwait(false);
            child.StandardInput.Close();
            var responseBytes = await ReadMessageAsync(child.StandardOutput.BaseStream, budget.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize(responseBytes, NativeUiaJsonContext.Default.NativeUiaResponse);
            await child.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            if (child.ExitCode != 0)
                result = new("NativeUiaHelperExitFailed", []);
            else if (response is null || response.Version != ProtocolVersion || response.Nonce != request.Nonce
                || !response.ParentIdentityMatched || response.Result is null || !ValidResult(response.Result))
                result = new("NativeUiaHelperProtocolFailed", []);
            else if (!MatchesProcess(child, childStarted, executable, alreadyExited: true)
                || !WindowOwnedBy((nint)request.WindowHandle, parent.Id))
                result = new("NativeUiaHelperIdentityChanged", []);
            else
                result = response.Result with
                {
                    HelperParentMatched = true,
                    HelperProcessMatched = true,
                    HelperExitedNormally = true,
                };
        }
        catch (OperationCanceledException) { result = new("NativeUiaHelperTimedOut", []); }
        catch (Exception) { result = new("NativeUiaHelperFailed", []); }
        finally
        {
            if (child is not null)
            {
                try
                {
                    // Only the retained process whose parent/start/path identity was verified may be killed.
                    if (childIdentityMatched && !child.HasExited && executable is not null
                        && MatchesProcess(child, childStarted, executable)
                        && ParentProcessId(child) == Environment.ProcessId)
                    {
                        child.Kill(entireProcessTree: false);
                        await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
                catch (Exception) { result = new("NativeUiaHelperCleanupFailed", []); }
                child.Dispose();
            }
        }
        return result;
    }

    // Program calls this before all WinRT/WinUI startup. Its STA entry awaits a separate MTA client.
    internal static async Task<int> RunHelperAsync()
    {
        using var budget = new CancellationTokenSource(QueryBudget);
        NativeUiaRequest? request = null;
        var response = new NativeUiaResponse();
        try
        {
            var input = await ReadMessageAsync(Console.OpenStandardInput(), budget.Token).ConfigureAwait(false);
            request = JsonSerializer.Deserialize(input, NativeUiaJsonContext.Default.NativeUiaRequest);
            if (request is null || !ValidRequest(request)) return 2;
            response.Version = ProtocolVersion;
            response.Nonce = request.Nonce;
            using var self = Process.GetCurrentProcess();
            if (ParentProcessId(self) != request.ParentProcessId) return 2;
            using var parent = Process.GetProcessById(request.ParentProcessId);
            if (Environment.ProcessPath is not { } executable
                || !MatchesProcess(parent, request.ParentStartedUtcTicks, executable)
                || self.StartTime.ToUniversalTime().Ticks < request.ParentStartedUtcTicks
                || !WindowOwnedBy((nint)request.WindowHandle, parent.Id)) return 2;
            response.ParentIdentityMatched = true;
            response.Result = await Task.Run(() => NativeUiaClient.Query(request), budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
            if (!MatchesProcess(parent, request.ParentStartedUtcTicks, executable)
                || !WindowOwnedBy((nint)request.WindowHandle, parent.Id))
            {
                response.ParentIdentityMatched = false;
                response.Result = new("NativeUiaParentIdentityChanged", []);
            }
            await WriteMessageAsync(Console.OpenStandardOutput(), response,
                NativeUiaJsonContext.Default.NativeUiaResponse, budget.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            // No exception Message, stack, environment or request is written to stdout/stderr.
            return 1;
        }
    }

    private static bool ValidRequest(NativeUiaRequest request) =>
        request.Version == ProtocolVersion && Guid.TryParseExact(request.Nonce, "N", out _)
        && request.FakeScopeVerified && request.ParentProcessId > 0 && request.ParentStartedUtcTicks > 0
        && request.WindowHandle != 0 && request.AutomationId is { Length: <= 1024 } && request.Name is { Length: <= 1024 }
        && (request.AutomationId.Length > 0 || request.Name.Length > 0)
        && double.IsFinite(request.Left) && double.IsFinite(request.Top)
        && double.IsFinite(request.Width) && double.IsFinite(request.Height) && request.Width > 0 && request.Height > 0;

    private static bool ValidResult(NativeUiaResult result) =>
        result.Status is { Length: > 0 and <= 64 } && result.Status.All(character => char.IsAsciiLetter(character))
        && result.Patterns is { Length: <= 2 } && result.Patterns.All(pattern => pattern is "Value" or "Text")
        && (result.LookupMode is "" or "AutomationIdAndEditAndOwnedProcess" or "NameAndEditAndOwnedProcess")
        && (result.Status != "Checked" || result.WindowProcessMatched && result.ControlProcessMatched && result.ControlType == 50004
            && result.Bounds is { } bounds && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
            && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0);

    private static bool MatchesProcess(Process process, long started, string executable, bool alreadyExited = false)
    {
        try
        {
            // The retained handle still identifies the exited process. Its image path was verified before input.
            return process.StartTime.ToUniversalTime().Ticks == started
                && (alreadyExited ? process.HasExited : !process.HasExited
                    && string.Equals(Path.GetFullPath(ImagePath(process.SafeHandle)), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private static bool WindowOwnedBy(nint window, int processId) =>
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)processId;

    private static unsafe string ImagePath(SafeProcessHandle process)
    {
        // The kernel records the image before CreateProcess returns; enumerating MainModule can
        // transiently fail before the loader publishes its module list. Query the retained handle.
        const int maximumPathCharacters = 32768;
        var buffer = stackalloc char[maximumPathCharacters];
        var length = (uint)maximumPathCharacters;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length) || length == 0 || length >= maximumPathCharacters)
            throw new InvalidOperationException("NativeUiaProcessImageUnavailable");
        return new string(buffer, 0, (int)length);
    }

    private static int ParentProcessId(Process process)
    {
        var status = NtQueryInformationProcess(process.SafeHandle, 0, out var information,
            (uint)Marshal.SizeOf<NativeProcessInformation>(), out _);
        return status == 0 && information.ParentProcessId > 0 && information.ParentProcessId <= (nuint)int.MaxValue
            ? (int)information.ParentProcessId : 0;
    }

    private static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumMessageBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var received = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (received == 0) throw new InvalidDataException("NativeUiaProtocolIncomplete");
            var end = Array.IndexOf(bytes, (byte)'\n', length, received);
            length += received;
            if (end >= 0)
            {
                if (end != length - 1) throw new InvalidDataException("NativeUiaProtocolTrailingData");
                return bytes.AsSpan(0, end).ToArray();
            }
        }
        throw new InvalidDataException("NativeUiaProtocolTooLarge");
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> metadata, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, metadata);
        if (bytes.Length >= MaximumMessageBytes) throw new InvalidDataException("NativeUiaProtocolTooLarge");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessInformation
    {
        public int ExitStatus;
        public nint Peb;
        public nuint Affinity;
        public int BasePriority;
        public nuint ProcessId, ParentProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out NativeProcessInformation information, uint length, out uint returnedLength);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* imageName, ref uint length);
}

internal sealed class NativeUiaRequest
{
    public int Version { get; set; }
    public string Nonce { get; set; } = "";
    public bool FakeScopeVerified { get; set; }
    public int ParentProcessId { get; set; }
    public long ParentStartedUtcTicks { get; set; }
    public long WindowHandle { get; set; }
    public string AutomationId { get; set; } = "";
    public string Name { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IncludeText { get; set; }
}

internal sealed class NativeUiaResponse
{
    public int Version { get; set; }
    public string Nonce { get; set; } = "";
    public bool ParentIdentityMatched { get; set; }
    public NativeUiaResult? Result { get; set; }
}

internal sealed record NativeUiaResult(string Status, string[] Patterns, int ControlType = 0,
    bool WindowProcessMatched = false, bool ControlProcessMatched = false,
    AccessibilityBoundsReport? Bounds = null, string LookupMode = "",
    bool HelperParentMatched = false, bool HelperProcessMatched = false, bool HelperExitedNormally = false);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(NativeUiaRequest))]
[JsonSerializable(typeof(NativeUiaResponse))]
internal sealed partial class NativeUiaJsonContext : JsonSerializerContext;
