using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.App.Composition;
using Mambo.App.Shell;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Playback;
using Mambo.Core.Reliability;
using Mambo.Core.Session;
using Mambo.Player.External;
using Mambo.Player.LibMpv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Dispatching;
using MpvValue = Mambo.Core.Playback.MpvValue;

namespace Mambo.App.Debug;

/// <summary>隔离账号及设置的外置接管回归；明确传入用户的 mpv 和本地片源，不访问 Emby。</summary>
internal static class ExternalHandoffSmoke
{
    internal const string Argument = "--external-handoff-smoke";
    private static readonly string[] SpaceCommand = ["keypress", "SPACE"];
    private static readonly string[] QuitCommand = ["quit"];

    internal static ServiceProvider CreateServices(DispatcherQueue queue)
    {
        var executable = Environment.GetEnvironmentVariable("MAMBO_EXTERNAL_MPV_PATH") ?? "";
        var sample = Environment.GetEnvironmentVariable("MAMBO_EXTERNAL_SAMPLE") ?? "";
        var report = Environment.GetEnvironmentVariable("MAMBO_EXTERNAL_REPORT") ?? "";
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(sample) ||
            !File.Exists(sample) || !Path.IsPathFullyQualified(report))
            throw new InvalidOperationException("ExternalHandoffInputInvalid");
        var scheduler = new UiScheduler(queue);
        var services = new ServiceCollection().AddBackendServices(true, scheduler,
            new FakeOptions { Delay = TimeSpan.FromMilliseconds(10), FailureRate = 0 });
        services.RemoveAll<IPlaybackService>();
        services.RemoveAll<ISettingsService>();
        services.AddSingleton(p => new Fixture(executable, new Uri(sample), report, scheduler, p.GetRequiredService<IMessenger>()));
        services.AddSingleton<IPlaybackService>(p => p.GetRequiredService<Fixture>().Playback);
        services.AddSingleton<ISettingsService>(p => p.GetRequiredService<Fixture>().Settings);
        return services.AddUiServices().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    internal static async Task RunAsync(MainWindow window)
    {
        var fixture = window.Services.GetRequiredService<Fixture>();
        var report = new ExternalHandoffReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var token = deadline.Token;
        var navigation = window.Services.GetRequiredService<Navigator>();
        var monitor = window.DispatcherQueue.CreateTimer();
        var playerAppeared = false;
        monitor.Interval = TimeSpan.FromMilliseconds(16);
        monitor.Tick += (_, _) => playerAppeared |= window.Shell.ActivePlayer is not null || window.Shell.IsPlayerFacing || window.Shell.CanHandle;
        try
        {
            await WaitAsync(() => window.Shell.IsLoaded, token);
            await fixture.Settings.ValidateExternalPlayerAsync(fixture.Executable, token);
            await fixture.Settings.UpdateAsync(value => value with { PlaybackMode = PlaybackMode.External }, token);
            var route = navigation.Current;
            monitor.Start();
            report.Stage = "ExternalLaunch";
            await window.Services.GetRequiredService<PlaybackLauncher>().PlayAsync("handoff-0");
            var session = fixture.Playback.Current ?? throw new InvalidOperationException("SessionMissing");
            Check(report, "ExternalIntentBeforeEngine", session.Snapshot.EngineKind == EngineKind.External);
            await WaitAsync(() => session.Snapshot.Phase is PlayerPhase.Playing or PlayerPhase.Closed, token);
            Check(report, "Playing", session.Snapshot.Phase == PlayerPhase.Playing);
            await WaitAsync(() => fixture.Handler.Reports.Any(value => value == "Playing"), token);
            var engine = fixture.External ?? throw new InvalidOperationException("ExternalEngineMissing");
            Check(report, "BrowsePagePreserved", ReferenceEquals(route, navigation.Current) && window.Shell.ActivePlayer is null);
            Check(report, "ConfigurationEnabled", await engine.GetAsync("options/config", token) is MpvValue.Flag { Value: true });
            Check(report, "ScriptsEnabled", await engine.GetAsync("options/load-scripts", token) is MpvValue.Flag { Value: true });
            Check(report, "KeyboardEnabled", await engine.GetAsync("options/input-vo-keyboard", token) is MpvValue.Flag { Value: true });
            Check(report, "DefaultBindingsEnabled", await engine.GetAsync("options/input-default-bindings", token) is MpvValue.Flag { Value: true });
            var bindings = await engine.GetAsync("input-bindings", token);
            report.CustomScriptBindingsLoaded = bindings is MpvValue.Array array && array.Values.OfType<MpvValue.Map>()
                .Any(binding => binding.Values.GetValueOrDefault("owner") is MpvValue.Text { Value: "uosc" });
            if (Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.Settings.Current.ExternalMpvPath!)!, "portable_config", "scripts", "uosc")))
                Check(report, "PortableUoscLoaded", report.CustomScriptBindingsLoaded);
            report.Stage = "NativePlaylist";
            await WaitAsync(async () => await engine.GetAsync("playlist-count", token) is MpvValue.WholeNumber { Value: 3 }, token);
            Check(report, "RemainingSeasonInMpv", true);
            report.PlaylistIdsBeforeJump = await ReadPlaylistIdsAsync(engine, token);
            Check(report, "UnplayedEpisodesAlreadyHaveTitles", (await ReadPlaylistTitlesAsync(engine, token)).SequenceEqual(LocalPreparer.Titles));
            Check(report, "InitialEntryOptions", await HasEntryOptionsAsync(engine, 0, token));
            await engine.CommandAsync(MoveEpisodeCommand, token);
            Check(report, "TitlesFollowNativeReorder", (await ReadPlaylistTitlesAsync(engine, token)).SequenceEqual(
                new[] { LocalPreparer.Titles[0], LocalPreparer.Titles[2], LocalPreparer.Titles[1] }));
            var paused = session.Snapshot.IsPaused;
            await engine.CommandAsync(SpaceCommand, token);
            await WaitAsync(() => session.Snapshot.IsPaused != paused, token);
            Check(report, "NativeSpaceControlsPlayback", true);
            await engine.SetAsync("pause", new MpvValue.Flag(false), token);
            await WaitAsync(() => !session.Snapshot.IsPaused, token);
            report.Stage = "NativeRate";
            await engine.SetAsync("speed", new MpvValue.Number(1.25), token);
            await WaitAsync(() => session.Snapshot.PlaybackRate == 1.25, token);
            report.Stage = "NativeJump";
            await engine.SetAsync("playlist-pos", new MpvValue.WholeNumber(1), token);
            await WaitAsync(() => session.Snapshot.CurrentEntryIndex == 2 && session.Snapshot.Phase == PlayerPhase.Playing &&
                fixture.Handler.Reports.Count(value => value == "Playing") >= 2, token);
            Check(report, "ReorderedEntryOptions", await HasEntryOptionsAsync(engine, 2, token));
            report.Stage = "NativeReplay";
            report.PlaylistIdsBeforeReplay = await ReadPlaylistIdsAsync(engine, token);
            await engine.SetAsync("playlist-pos", new MpvValue.WholeNumber(0), token);
            await WaitAsync(() => session.Snapshot.CurrentEntryIndex == 0 && session.Snapshot.Phase == PlayerPhase.Playing &&
                fixture.Handler.Reports.Count(value => value == "Playing") >= 3, token);
            Check(report, "NativeSelectionAndReplay", session.Snapshot.PlaybackRate == 1.25);
            Check(report, "ReplayRestoresOwnOptions", await HasEntryOptionsAsync(engine, 0, token));
            Check(report, "TitlesSurviveReplay", (await ReadPlaylistTitlesAsync(engine, token)).SequenceEqual(
                new[] { LocalPreparer.Titles[0], LocalPreparer.Titles[2], LocalPreparer.Titles[1] }));
            navigation.Navigate(Route.Settings);
            Check(report, "BrowsingRemainsAvailable", navigation.Current.Route == Route.Settings && !navigation.ForwardBlocked);
            report.Stage = "NativeQuit";
            try { await engine.CommandAsync(QuitCommand, token); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            await WaitAsync(() => fixture.Playback.Current is null && session.Snapshot.Phase == PlayerPhase.Closed, token);
            Check(report, "QuitClosesSession", session.Snapshot.Error is null);
            Check(report, "StoppedExactlyOncePerPlayback", fixture.Handler.Reports.Count(value => value == "Stopped") == 3 && fixture.Outbox.Snapshot.IsEmpty);
            monitor.Stop();
            Check(report, "NoMamboPlayerDuringExternalSession", !playerAppeared);
            report.Stage = "EmbeddedFallback";
            fixture.UseEmbedded = true;
            var fallback = await fixture.Playback.PlayAsync(new("handoff-0"), token);
            await WaitAsync(() => fallback.Snapshot.EngineKind == EngineKind.Embedded && window.Shell.ActivePlayer is { IsLoaded: true }, token);
            Check(report, "FallbackPresentsEmbeddedPlayer", ReferenceEquals(window.Shell.ActivePlayer!.Session, fallback));
            await fallback.CloseAsync(token);
            await window.Shell.PendingPresentation;
            Check(report, "FallbackClosesCleanly", window.Shell.ActivePlayer is null && !navigation.ForwardBlocked);
            report.Passed = true;
            report.Stage = "Complete";
        }
        catch (Exception error)
        {
            report.ErrorKind = error.GetType().Name;
            report.ErrorCode = (error as AppException)?.Error.Code ?? "";
            report.LastPhase = fixture.Playback.Current?.Snapshot.Phase.ToString() ?? "";
            report.LastEntryIndex = fixture.Playback.Current?.Snapshot.CurrentEntryIndex ?? -1;
            report.Reports = fixture.Handler.Reports.ToArray();
        }
        finally
        {
            monitor.Stop();
            try
            {
                if (fixture.Playback.Current is { } session) await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await window.CloseForSmokeAsync().WaitAsync(TimeSpan.FromSeconds(12));
                Check(report, "ShutdownCompleted", window.HasCompletedShutdownForSmoke);
            }
            catch (Exception error) { report.Passed = false; report.CleanupErrorKind = error.GetType().Name; }
            report.NativeEvents = fixture.NativeEvents.ToArray();
            await File.WriteAllBytesAsync(fixture.ReportPath, JsonSerializer.SerializeToUtf8Bytes(report, ExternalHandoffJsonContext.Default.ExternalHandoffReport));
            if (!report.Passed) Environment.ExitCode = 1;
            if (window.HasCompletedShutdownForSmoke) window.Close();
        }
    }

    private static void Check(ExternalHandoffReport report, string name, bool passed)
    {
        report.Checks[name] = passed;
        if (!passed) { report.Stage = name; throw new InvalidOperationException("ExternalHandoffCheckFailed"); }
    }
    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (!ready()) await Task.Delay(20, deadline.Token);
    }
    private static async Task WaitAsync(Func<Task<bool>> ready, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (!await ready()) await Task.Delay(20, deadline.Token);
    }

    private static async Task<long[]> ReadPlaylistIdsAsync(ExternalMpvEngine engine, CancellationToken token) =>
        await engine.GetAsync("playlist", token) is MpvValue.Array list ? list.Values.OfType<MpvValue.Map>()
            .Select(entry => entry.Values.GetValueOrDefault("id") is MpvValue.WholeNumber id ? id.Value : -1).ToArray() : [];

    private static readonly string[] MoveEpisodeCommand = ["playlist-move", "2", "1"];

    private static async Task<string[]> ReadPlaylistTitlesAsync(ExternalMpvEngine engine, CancellationToken token) =>
        await engine.GetAsync("playlist", token) is MpvValue.Array list ? list.Values.OfType<MpvValue.Map>()
            .Select(entry => entry.Values.GetValueOrDefault("title") is MpvValue.Text title ? title.Value : "").ToArray() : [];

    private static async Task<bool> HasEntryOptionsAsync(ExternalMpvEngine engine, int index, CancellationToken token)
    {
        var title = await engine.GetAsync("media-title", token);
        var headers = await engine.GetAsync("options/http-header-fields", token);
        var position = await engine.GetAsync("time-pos", token);
        var seconds = position switch { MpvValue.Number value => value.Value, MpvValue.WholeNumber value => value.Value, _ => -1 };
        var expectedHeaders = LocalPreparer.Headers[index] is { Length: > 0 } header ? new[] { header } : [];
        return title is MpvValue.Text name && name.Value == LocalPreparer.Titles[index] &&
            headers is MpvValue.Array values && values.Values.OfType<MpvValue.Text>().Select(value => value.Value).SequenceEqual(expectedHeaders) &&
            seconds >= LocalPreparer.Starts[index] && seconds < LocalPreparer.Starts[index] + 8;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AccountContext accounts = new();
        private readonly EmbyApi api;
        public string Executable { get; }
        public string ReportPath { get; }
        public SettingsStore Settings { get; }
        public PlaybackCoordinator Playback { get; }
        public ReportHandler Handler { get; } = new();
        public StopOutbox Outbox { get; }
        public bool UseEmbedded { get; set; }
        public ExternalMpvEngine? External { get; private set; }
        public ConcurrentQueue<string> NativeEvents { get; } = new();

        public Fixture(string executable, Uri sample, string report, IUiScheduler scheduler, IMessenger messenger)
        {
            if (!sample.IsFile) throw new InvalidOperationException("LocalSampleRequired");
            Executable = executable; ReportPath = report;
            var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(report)!, "state-" + Guid.NewGuid().ToString("N")));
            var validator = new MpvExecutableApproval();
            Settings = new(paths, scheduler, validator);
            accounts.Set(new(new SessionSecret("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "外置播放诊断", Guid.NewGuid().ToString("N"))));
            api = new(Settings.Current.DeviceId, Handler);
            Outbox = new(paths, api);
            Playback = new(accounts, new LocalPreparer(sample), async cancellation =>
            {
                if (UseEmbedded) return await LibMpvEngine.CreateAsync(1, 1, cancellationToken: cancellation).ConfigureAwait(false);
                var approval = await Settings.GetApprovedExternalPlayerAsync(cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("ExternalApprovalMissing");
                External = await ExternalMpvEngine.CreateAsync(approval, validator, cancellationToken: cancellation).ConfigureAwait(false);
                return (IPlayerEngine)new ObservedEngine(External, NativeEvents);
            }, api, Outbox, Settings, scheduler, messenger, TimeProvider.System);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Playback.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                await Outbox.DisposeAsync().ConfigureAwait(false);
                Settings.Dispose(); api.Dispose(); accounts.Dispose();
            }
        }
    }

    private sealed class ObservedEngine : IPlayerEngine
    {
        private readonly ExternalMpvEngine source;
        private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>();
        private readonly Task forwarding;
        public ObservedEngine(ExternalMpvEngine source, ConcurrentQueue<string> trace)
        {
            this.source = source;
            forwarding = ForwardAsync(trace);
        }
        public EngineKind Kind => EngineKind.External;
        public ChannelReader<EngineEvent> Events => events.Reader;
        private async Task ForwardAsync(ConcurrentQueue<string> trace)
        {
            await foreach (var item in source.Events.ReadAllAsync().ConfigureAwait(false))
            {
                var text = item switch
                {
                    EngineEvent.StartFile start => "Start:" + start.EntryId.ToString(CultureInfo.InvariantCulture),
                    EngineEvent.EndFile end => "End:" + end.EntryId.ToString(CultureInfo.InvariantCulture) + ":" + end.Reason,
                    EngineEvent.FileLoaded => "Loaded", EngineEvent.PlaybackRestart => "Restart", EngineEvent.Shutdown => "Shutdown", _ => null,
                };
                if (text is not null) trace.Enqueue(text);
                events.Writer.TryWrite(item);
            }
            events.Writer.TryComplete();
        }
        public ValueTask<long> LoadAsync(string url, LoadMode mode, IReadOnlyList<KeyValuePair<string, string>> options, CancellationToken cancellationToken) => source.LoadAsync(url, mode, options, cancellationToken);
        public ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken) => source.CommandAsync(arguments, cancellationToken);
        public ValueTask SetAsync(string propertyName, MpvValue value, CancellationToken cancellationToken) => source.SetAsync(propertyName, value, cancellationToken);
        public async ValueTask DisposeAsync() { await source.DisposeAsync().ConfigureAwait(false); await forwarding.ConfigureAwait(false); }
    }

    private sealed class ReportHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Reports { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Reports.Enqueue(path.EndsWith("/Stopped", StringComparison.Ordinal) ? "Stopped" :
                path.EndsWith("/Progress", StringComparison.Ordinal) ? "Progress" : "Playing");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class LocalPreparer(Uri sample) : IEntryPreparer
    {
        internal static readonly string[] Titles = ["外置验证 S01E01 - 起点", "外置验证 S01E02 - 名称,与符号🎬", "外置验证 S01E03 - 下一站"];
        internal static readonly double[] Starts = [1.25, 2.25, 3.25];
        internal static readonly string[] Headers = ["X-Mambo-Test: " + Guid.NewGuid().ToString("N"), "X-Mambo-Test: " + Guid.NewGuid().ToString("N"), ""];
        private static readonly ImmutableArray<PlaybackEntry> Entries = Enumerable.Range(0, 3)
            .Select(index => new PlaybackEntry("handoff-" + index.ToString(CultureInfo.InvariantCulture), Titles[index])).ToImmutableArray();
        public Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken) => Task.FromResult(new PreparedPlan(Entries, 0, 0));
        public Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
        {
            var source = new EmbyMediaSource { Id = Guid.NewGuid().ToString("N") };
            return Task.FromResult(new PreparedEntry(entry, new() { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSources = [source] },
                [new(sample, "DirectPlay", source, ImmutableDictionary<string, string>.Empty)], [], startTicks));
        }
        public Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
        {
            var index = Entries.IndexOf(entry.Entry);
            return Task.FromResult(new ResolvedCandidate(entry.Candidates[candidateIndex], new(sample, ImmutableDictionary<string, string>.Empty, 0),
                [new("force-media-title", entry.Entry.Title), new("start", Starts[index].ToString(CultureInfo.InvariantCulture)), new("http-header-fields", Headers[index])]));
        }
        public Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<ResolvedSubtitle>.Empty);
        public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles) => Task.CompletedTask;
    }
}

internal sealed class ExternalHandoffReport
{
    public bool Passed { get; set; }
    public string Stage { get; set; } = "Setup";
    public Dictionary<string, bool> Checks { get; set; } = [];
    public bool CustomScriptBindingsLoaded { get; set; }
    public string ErrorKind { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string CleanupErrorKind { get; set; } = "";
    public string LastPhase { get; set; } = "";
    public int LastEntryIndex { get; set; } = -1;
    public string[] Reports { get; set; } = [];
    public string[] NativeEvents { get; set; } = [];
    public long[] PlaylistIdsBeforeJump { get; set; } = [];
    public long[] PlaylistIdsBeforeReplay { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExternalHandoffReport))]
internal sealed partial class ExternalHandoffJsonContext : JsonSerializerContext;
