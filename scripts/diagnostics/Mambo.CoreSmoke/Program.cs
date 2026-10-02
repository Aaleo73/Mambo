using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.App.Platform;
using Mambo.Core;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Reliability;
using Mambo.Core.Session;

namespace Mambo.CoreSmoke;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Contains("--self-test", StringComparer.Ordinal)) return await SelfTestAsync(args, cancellation.Token).ConfigureAwait(false);
            using var scheduler = new ConsoleScheduler();
            await using var backend = new BackendRuntime(new AppPaths(), new WindowsCredentialStore(), scheduler, new WeakReferenceMessenger());
            if (args.Contains("--login", StringComparer.Ordinal))
            {
                Console.Write("服务器地址（仅在本地输入）："); var address = Console.ReadLine() ?? "";
                Console.Write("用户名："); var user = Console.ReadLine() ?? "";
                Console.Write("密码（隐藏，可为空）："); var password = ReadPassword(); Console.WriteLine();
                await backend.Session.LoginAsync(new LoginRequest(address, user, password), cancellation.Token).ConfigureAwait(false);
                await backend.Settings.SaveConnectionDefaultsAsync(new ConnectionDefaults(address, user), cancellation.Token).ConfigureAwait(false);
            }
            else await backend.Session.RestoreAsync(cancellation.Token).ConfigureAwait(false);
            if (args.Contains("--logout", StringComparer.Ordinal))
            {
                var result = await backend.Session.LogoutAsync(cancellation.Token).ConfigureAwait(false);
                Console.WriteLine(result.RemoteLogoutFailed ? "本地已断开，但服务器会话可能仍有效。" : "已断开并清除本地凭据和账号缓存。");
                return 0;
            }
            if (backend.Session.State != SessionState.LoggedIn) { Console.WriteLine("没有可用会话，请先以 --login 运行。"); return 2; }
            using var libraries = backend.Library.ObserveLibraries(cancellation.Token);
            await libraries.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            if (libraries.Error is { } error) throw new AppException(error);
            Console.WriteLine("会话可用；视频资料库数量：" + libraries.Current.Length);
            Console.WriteLine("凭据目标：" + WindowsCredentialStore.TargetName);
            Console.WriteLine("请退出后以 --restore 再次运行，并在 Windows 凭据管理器中确认该目标存在。");
            return 0;
        }
        catch (OperationCanceledException) { Console.WriteLine("操作已取消。"); return 3; }
        catch (AppException error) { Console.Error.WriteLine(error.Error.Message); return 1; }
        catch (Exception error) { Console.Error.WriteLine("验收工具失败（" + error.GetType().Name + "）；未输出服务器信息。"); return 1; }
    }
    private static string ReadPassword()
    {
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) return result.ToString();
            if (key.Key == ConsoleKey.Backspace) { if (result.Length > 0) result.Length--; }
            else if (!char.IsControl(key.KeyChar)) result.Append(key.KeyChar);
        }
    }
    private static async Task<int> SelfTestAsync(string[] args, CancellationToken token)
    {
        var report = new SmokeReport();
        var index = Array.IndexOf(args, "--report");
        var reportPath = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
        var rootIndex = Array.IndexOf(args, "--data-root");
        if (rootIndex < 0 || rootIndex + 1 >= args.Length) throw new ArgumentException("自测必须指定隔离的数据目录。");
        var paths = new AppPaths(args[rootIndex + 1]);
        var fixture = new Fixture();
        var secrets = new MemorySecrets();
        using var scheduler = new ConsoleScheduler();
        try
        {
            report.Stage = "登录与原子设置";
            Guid device;
            await using (var first = new BackendRuntime(paths, secrets, scheduler, new WeakReferenceMessenger(), apiHandler: fixture.Handler(), imageHandler: fixture.Handler()))
            {
                await first.Session.LoginAsync(new LoginRequest(fixture.Address, "测试用户", Guid.NewGuid().ToString("N")), token).ConfigureAwait(false);
                report.Login = first.Session.State == SessionState.LoggedIn && secrets.Value is not null;
                device = first.Settings.Current.DeviceId;
                await first.Settings.UpdateAsync(value => value with { Volume = 63 }, token).ConfigureAwait(false);
                first.Log.Error("平台组合自测诊断", new(AppErrorKind.Contract, "smoke.diagnostic", "验证日志落盘与脱敏。", false));
                first.Log.Exception("平台组合自测异常", new InvalidOperationException(fixture.AccessToken));
                var preview = await first.Playback.PreviewAsync(token).ConfigureAwait(false);
                using (var previewBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    previewBudget.CancelAfter(TimeSpan.FromSeconds(5));
                    while (preview.Snapshot.Phase != PlayerPhase.Playing) await Task.Delay(1, previewBudget.Token).ConfigureAwait(false);
                    await preview.SetVolumeAsync(48, previewBudget.Token).ConfigureAwait(false);
                    while (first.Settings.Current.Volume != 48) await Task.Delay(1, previewBudget.Token).ConfigureAwait(false);
                }
                report.Volume = first.Settings.Current.Volume == 48;
                await preview.CloseAsync(token).ConfigureAwait(false);
                report.Stage = "资料库与发件箱";
                using var libraries = first.Library.ObserveLibraries(token);
                await libraries.RefreshAsync(token).ConfigureAwait(false);
                report.Library = libraries.IsInitialized && libraries.Error is null && libraries.Current.Length == 1;
                var account = first.Accounts.Current!;
                await first.Outbox.QueueAsync(new StopReportRecord(account.Secret.ServerId, account.Address.Uri.AbsoluteUri, account.Secret.UserId, Guid.NewGuid().ToString("N"), fixture.ItemId, 0, 0), token).ConfigureAwait(false);
                await first.Outbox.FlushAsync(account, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                report.Outbox = first.Outbox.Snapshot.Length == 0 && fixture.Stopped > 0;
                await first.QueryCache.FlushAsync(token).ConfigureAwait(false);
            }
            report.Stage = "重启恢复与缓存";
            await using (var restored = new BackendRuntime(paths, secrets, scheduler, new WeakReferenceMessenger(), apiHandler: fixture.Handler(), imageHandler: fixture.Handler()))
            {
                await restored.Session.RestoreAsync(token).ConfigureAwait(false);
                report.Restore = restored.Session.State == SessionState.LoggedIn;
                report.Settings = restored.Settings.Current.DeviceId == device && restored.Settings.Current.Volume == 48;
                using var libraries = restored.Library.ObserveLibraries(token);
                report.Cached = libraries.IsInitialized && libraries.Current.Length == 1;
                report.Stage = "注销";
                var result = await restored.Session.LogoutAsync(token).ConfigureAwait(false);
                report.Logout = !result.RemoteLogoutFailed && secrets.Value is null && restored.Accounts.Current is null;
            }
            report.Log = Directory.EnumerateFiles(paths.Logs, "*.log").Any(path => File.ReadAllText(path).Contains("平台组合自测诊断", StringComparison.Ordinal));
            report.SecretSafe = !Directory.EnumerateFiles(paths.Root, "*", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains(fixture.AccessToken, StringComparison.Ordinal));
            report.Passed = report.Login && report.Library && report.Outbox && report.Restore && report.Settings && report.Volume && report.Cached && report.Logout && report.Log && report.SecretSafe;
            report.Stage = "完成";
        }
        catch (AppException error) { report.Error = error.Error.Code; }
        catch (Exception error) { report.Error = error.GetType().Name; }
        if (reportPath.Length > 0) await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, SmokeJsonContext.Default.SmokeReport), token).ConfigureAwait(false);
        Console.WriteLine(report.Passed ? "P1 平台组合自测通过（合成服务器与内存凭据）。" : "P1 自测失败：" + report.Stage + " / " + report.Error);
        return report.Passed ? 0 : 1;
    }
    private sealed class ConsoleScheduler : IUiScheduler, IDisposable
    {
        private readonly BlockingCollection<Action> callbacks = new();
        private readonly Thread worker;
        public ConsoleScheduler()
        {
            worker = new Thread(() => { foreach (var callback in callbacks.GetConsumingEnumerable()) { try { callback(); } catch { Console.Error.WriteLine("通知处理失败。"); } } }) { IsBackground = true, Name = "Mambo.CoreSmoke.Notifications" };
            worker.Start();
        }
        public bool TryEnqueue(Action callback) { try { callbacks.Add(callback); return true; } catch (InvalidOperationException) { return false; } }
        public void Dispose() { callbacks.CompleteAdding(); worker.Join(); callbacks.Dispose(); }
    }
    private sealed class MemorySecrets : ISecretStore
    {
        public SessionSecret? Value { get; private set; }
        public Task<SessionSecret?> ReadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
        public Task WriteAsync(SessionSecret secret, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Value = secret; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Value = null; return Task.CompletedTask; }
    }
    private sealed class Fixture
    {
        public string Address { get; } = "https://" + Guid.NewGuid().ToString("N") + ".invalid/emby";
        public string AccessToken { get; } = Guid.NewGuid().ToString("N");
        public string ItemId { get; } = Guid.NewGuid().ToString("N");
        private readonly string userId = Guid.NewGuid().ToString("N"), serverId = Guid.NewGuid().ToString("N");
        public int Stopped;
        public HttpMessageHandler Handler() => new FixtureHandler(this);
        private sealed class FixtureHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("AuthenticateByName", StringComparison.Ordinal)) return Task.FromResult(Json(new EmbyAuthentication { AccessToken = fixture.AccessToken, ServerId = fixture.serverId, User = new EmbyItem { Id = fixture.userId, Name = "测试用户" } }, EmbyJsonContext.Default.EmbyAuthentication));
                if (path.EndsWith("/Views", StringComparison.Ordinal)) return Task.FromResult(Json(new EmbyItems { Items = [new EmbyItem { Id = fixture.ItemId, Name = "测试电影库", Type = "CollectionFolder", CollectionType = "movies" }], TotalRecordCount = 1 }, EmbyJsonContext.Default.EmbyItems));
                if (path.EndsWith("/Stopped", StringComparison.Ordinal)) Interlocked.Increment(ref fixture.Stopped);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, info)) };
        }
    }
}
internal sealed class SmokeReport
{
    public bool Passed { get; set; } public bool Login { get; set; } public bool Library { get; set; }
    public bool Outbox { get; set; } public bool Restore { get; set; } public bool Settings { get; set; } public bool Volume { get; set; }
    public bool Cached { get; set; } public bool Logout { get; set; } public bool Log { get; set; } public bool SecretSafe { get; set; }
    public string Stage { get; set; } = ""; public string Error { get; set; } = "";
}
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SmokeReport))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
