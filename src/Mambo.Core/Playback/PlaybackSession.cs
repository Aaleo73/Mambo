using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Channels;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

/// <summary>只有收件箱读循环改变播放状态；准备工作完成后投回收件箱。</summary>
public sealed class PlaybackSession : IPlaybackSession, IAsyncDisposable
{
    private static readonly string[] RemoveOldEntryCommand = ["playlist-remove", "0"];
    private static readonly string[] StopCommand = ["stop"];
    private static readonly string[] NextCommand = ["playlist-next"];
    private readonly PlayRequest request;
    private readonly AccountSession account;
    private readonly IEntryPreparer preparer;
    private readonly Func<CancellationToken, Task<IPlayerEngine>> factory;
    private readonly PlaybackReporter reporter;
    private readonly IUiScheduler scheduler;
    private readonly TimeProvider clock;
    private readonly Action<PlaybackSession, PlaybackEndReason> ended;
    private readonly Action<PlaybackEntry, AppError> skipped;
    private readonly Action<AppError>? log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Input> inbox = Channel.CreateUnbounded<Input>(new() { SingleReader = true });
    private readonly Dictionary<long, LoadedEntry> loaded = [];
    private readonly HashSet<LoadedEntry> preparedEntries = [];
    private readonly Dictionary<int, int> preparing = [];
    private readonly List<Task> background = [];
    private readonly List<Task> preparationWork = [];
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionSnapshot snapshot;
    private IPlayerEngine? engine;
    private LoadedEntry? active;
    private LoadedEntry? appended;
    private ITimer? progressTimer, slowTimer, closeTimer;
    private int generation, consecutiveSkips, notificationQueued, throttleScheduled;
    private bool closing, finalized, switching, hasConfirmedPlayback;
    private ITimer? notificationTimer;
    private PlayRequest? retryRequest;
    private PlaybackEndReason closeReason;
    private Task lastStop = Task.CompletedTask;
    private double desiredVolume;
    private double desiredRate = 1;
    private bool desiredMuted;
    private Exception? closeError;
    private readonly Task actor;

    public PlaybackSession(PlayRequest request, AccountSession account, IEntryPreparer preparer,
        Func<CancellationToken, Task<IPlayerEngine>> factory, PlaybackReporter reporter,
        IUiScheduler scheduler, TimeProvider clock, double volume,
        Action<PlaybackSession, PlaybackEndReason> ended, Action<PlaybackEntry, AppError> skipped,
        Action<AppError>? log = null)
    {
        this.request = request; this.account = account; this.preparer = preparer; this.factory = factory;
        this.reporter = reporter; this.scheduler = scheduler; this.clock = clock;
        this.ended = ended; this.skipped = skipped; this.log = log;
        desiredVolume = volume;
        snapshot = new() { Entry = new(request.ItemId, "正在准备播放"), Volume = volume, CapturedAtUtc = clock.GetUtcNow() };
        actor = RunAsync();
    }

    public SessionSnapshot Snapshot => Volatile.Read(ref snapshot);
    public IPlayerEngine? Engine => Volatile.Read(ref engine);
    public ISettingsService? Settings { get; internal set; }
    public event EventHandler? SnapshotChanged;
    // 这些是后端桥接 API，前端只调用 VideoSurface.Attach/Detach。
    public event Action<IPlayerEngine?>? EngineChanged;
    public event Func<Task>? Detaching;
    public Task Completion => actor;
    internal string RequestedItemId => request.ItemId;
    internal void Start() => Post(() => { Initialize(); return Task.CompletedTask; });

    private async Task RunAsync()
    {
        await foreach (var input in inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await input.Action().ConfigureAwait(false); input.Completion?.TrySetResult(); }
            catch (OperationCanceledException) { input.Completion?.TrySetCanceled(); }
            catch (Exception exception)
            {
                var error = SafeError(exception);
                if (input.Completion is { } completion) completion.TrySetException(new AppException(error));
                else if (!closing) Fail(error);
                else log?.Invoke(error);
            }
        }
        if (closeError is { } failure) closed.TrySetException(new AppException(SafeError(failure)));
        else closed.TrySetResult();
    }

    private bool Post(Func<Task> action) => inbox.Writer.TryWrite(new(action));
    private Task Command(Func<Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inbox.Writer.TryWrite(new(async () =>
        {
            if (closing) throw new AppException(new(AppErrorKind.Contract, ErrorCodes.SessionClosed, "播放会话已关闭。", false));
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }, completion))) throw new AppException(new(AppErrorKind.Contract, ErrorCodes.SessionClosed, "播放会话已关闭。", false));
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void Initialize()
    {
        var epoch = ++generation;
        Update(snapshot with { Phase = PlayerPhase.Preparing, Error = null, IsSlowOpening = false });
        TrackPreparation(InitializeAsync(epoch));
    }
    private async Task InitializeAsync(int epoch)
    {
        IPlayerEngine? created = null;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        Task<IPlayerEngine>? creatingEngine = null;
        try
        {
            var resolvingPlan = preparer.ResolvePlanAsync(account, retryRequest ?? request, preparation.Token);
            creatingEngine = factory(preparation.Token);
            await Task.WhenAll(resolvingPlan, creatingEngine).ConfigureAwait(false);
            var plan = await resolvingPlan.ConfigureAwait(false);
            created = await creatingEngine.ConfigureAwait(false);
            if (plan.Entries.IsDefaultOrEmpty || plan.SelectedIndex < 0 || plan.SelectedIndex >= plan.Entries.Length || plan.StartTicks < 0)
                throw InvalidCommand();
            if (preparation.IsCancellationRequested) { await created.DisposeAsync().ConfigureAwait(false); return; }
            var owned = created;
            if (!Post(async () =>
            {
                if (closing || epoch != generation) { await owned.DisposeAsync().ConfigureAwait(false); return; }
                engine = owned;
                // 新引擎的默认属性不能覆盖保存的音量或本会话重试前的控制状态。
                await owned.SetAsync("volume", new MpvValue.Number(desiredVolume), lifetime.Token).ConfigureAwait(false);
                await owned.SetAsync("speed", new MpvValue.Number(desiredRate), lifetime.Token).ConfigureAwait(false);
                await owned.SetAsync("mute", new MpvValue.Flag(desiredMuted), lifetime.Token).ConfigureAwait(false);
                scheduler.TryEnqueue(() => { if (Engine == owned && !finalized) EngineChanged?.Invoke(owned); });
                background.Add(ConsumeAsync(owned));
                Update(snapshot with { Entries = plan.Entries, CurrentEntryIndex = plan.SelectedIndex,
                    Entry = plan.Entries[plan.SelectedIndex], EngineKind = owned.Kind, PositionTicks = plan.StartTicks });
                Prepare(plan.SelectedIndex, plan.StartTicks, append: false);
            })) await owned.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (created is null && creatingEngine is { IsCompletedSuccessfully: true }) created = creatingEngine.Result;
            if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
            Post(() => { if (!closing && epoch == generation) Fail(SafeError(exception)); return Task.CompletedTask; });
        }
    }
    private async Task ConsumeAsync(IPlayerEngine owner)
    {
        var shutdownSeen = false;
        try
        {
            await foreach (var message in owner.Events.ReadAllAsync().ConfigureAwait(false))
            {
                shutdownSeen |= message is EngineEvent.Shutdown;
                Post(() => ReferenceEquals(owner, engine) ? HandleAsync(message) : Task.CompletedTask);
            }
        }
        catch (Exception) { /* 引擎结束与显式 Shutdown 走同一条状态路径。 */ }
        if (!shutdownSeen) Post(() => ReferenceEquals(owner, engine) ? HandleAsync(new EngineEvent.Shutdown()) : Task.CompletedTask);
    }

    private void Prepare(int index, long startTicks, bool append)
    {
        if (closing || index < 0 || index >= snapshot.Entries.Length) return;
        var epoch = generation;
        if (preparing.GetValueOrDefault(index, -1) == epoch) return;
        preparing[index] = epoch;
        var entry = snapshot.Entries[index];
        if (!append) { switching = true; Update(snapshot with { Phase = active?.Confirmed == true ? PlayerPhase.Interstitial : PlayerPhase.Preparing, Error = null }); }
        TrackPreparation(PrepareAsync(epoch, entry, index, startTicks, append));
    }
    private async Task PrepareAsync(int epoch, PlaybackEntry entry, int index, long startTicks, bool append)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        try
        {
            var ready = await preparer.PrepareAsync(account, entry, startTicks, preparation.Token).ConfigureAwait(false);
            Post(() => closing || epoch != generation ? Task.CompletedTask : ResolveAsync(new(ready, index), 0, append));
        }
        catch (Exception exception)
        { Post(() => { if (!closing && epoch == generation) PreparationFailed(entry, index, append, SafeError(exception)); return Task.CompletedTask; }); }
    }
    private Task ResolveAsync(LoadedEntry entry, int candidateIndex, bool append)
    {
        if (candidateIndex >= entry.Prepared.Candidates.Length)
        {
            var nativeFailure = entry.LastNativeFailure;
            var diagnosticId = nativeFailure?.DiagnosticId ?? Guid.NewGuid().ToString("N");
            var message = nativeFailure?.Status is { } nativeError
                ? $"片源无法播放，请重试。（错误代码：{nativeError.ToString(CultureInfo.InvariantCulture)}）"
                : "片源无法播放，请重试。";
            PreparationFailed(entry.Prepared.Entry, entry.Index, append, new(AppErrorKind.Player, ErrorCodes.PlaybackFailed,
                message, true, "playback.candidates.exhausted", nativeFailure?.Status, diagnosticId));
            return Task.CompletedTask;
        }
        entry.CandidateIndex = candidateIndex;
        preparedEntries.Add(entry);
        var epoch = generation;
        TrackPreparation(ResolveWorkerAsync(epoch, entry, candidateIndex, append));
        return Task.CompletedTask;
    }
    private async Task ResolveWorkerAsync(int epoch, LoadedEntry entry, int candidateIndex, bool append)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        try
        {
            if (candidateIndex > 0 && entry.Candidate?.Candidate.PlayMethod == "Transcode" &&
                entry.Prepared.PlaybackInfo.PlaySessionId is { Length: > 0 } abandoned)
                await reporter.CleanupAsync(abandoned, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            var candidate = await preparer.ResolveCandidateAsync(account, entry.Prepared, candidateIndex, preparation.Token).ConfigureAwait(false);
            Post(() => closing || epoch != generation ? Task.CompletedTask : LoadAsync(entry, candidate, append));
        }
        catch (Exception exception)
        {
            if (entry.Prepared.Candidates[candidateIndex].PlayMethod == "Transcode" &&
                entry.Prepared.PlaybackInfo.PlaySessionId is { Length: > 0 } failedSession)
                await reporter.CleanupAsync(failedSession, TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            Post(() =>
            {
                if (closing || epoch != generation) return Task.CompletedTask;
                log?.Invoke(SafeError(exception));
                return ResolveAsync(entry, candidateIndex + 1, append);
            });
        }
    }
    private async Task LoadAsync(LoadedEntry entry, ResolvedCandidate candidate, bool append)
    {
        if (engine is null) return;
        try
        {
            append &= active is { Confirmed: true, Ended: false };
            entry.Candidate = candidate;
            entry.NativeId = await engine.LoadAsync(candidate.Url.Address.AbsoluteUri,
                append ? LoadMode.Append : LoadMode.Replace, candidate.FileOptions, lifetime.Token).ConfigureAwait(false);
            if (entry.NativeId < 0) throw new InvalidOperationException("播放器未返回播放条目标识。");
            entry.NativeFailureLogged = false;
            entry.Loaded = false;
            entry.WasAppended = append;
            loaded[entry.NativeId] = entry;
            preparing.Remove(entry.Index);
            if (append) appended = entry;
            else
            {
                switching = false;
                Update(snapshot with { Phase = PlayerPhase.Opening, IsSlowOpening = false });
                slowTimer?.Dispose();
                var id = entry.NativeId;
                slowTimer = clock.CreateTimer(_ => Post(() =>
                {
                    if (!closing && loaded.TryGetValue(id, out var slow) && !slow.Confirmed)
                        Update(snapshot with { IsSlowOpening = true });
                    return Task.CompletedTask;
                }), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception exception)
        {
            log?.Invoke(SafeError(exception));
            await ResolveAsync(entry, entry.CandidateIndex + 1, append).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(EngineEvent message)
    {
        if (finalized) return;
        switch (message)
        {
            case EngineEvent.StartFile start when loaded.TryGetValue(start.EntryId, out var entry):
                if (closing) break;
                switching = false;
                // START_FILE 是权威条目标识；playlist-pos 只描述播放器内部列表。
                if (active is { Ended: false } old && old != entry) Stop(old);
                active = entry; appended = appended == entry ? null : appended;
                entry.PositionTicks = entry.Prepared.StartTicks;
                Update(snapshot with { Entry = entry.Prepared.Entry, CurrentEntryIndex = entry.Index,
                    PositionTicks = entry.PositionTicks, DurationTicks = entry.Prepared.Entry.DurationTicks ?? 0,
                    Phase = PlayerPhase.Opening, Error = null, AudioTracks = [], SubtitleTracks = [],
                    SelectedAudioTrackId = null, SelectedSubtitleTrackId = null, BufferedRanges = [], IsSlowOpening = false });
                if (entry.WasAppended && engine is not null)
                {
                    entry.WasAppended = false;
                    // 自动切集后移除已经结束的第一项，只保留当前项与随后追加项。
                    if (loaded.Values.Any(value => value.Ended && value.Index < entry.Index))
                        await engine.CommandAsync(RemoveOldEntryCommand, lifetime.Token).ConfigureAwait(false);
                }
                break;
            case EngineEvent.FileLoaded when active is { } current && !closing:
                current.Loaded = true;
                if (engine is not null)
                {
                    await engine.SetAsync("volume", new MpvValue.Number(desiredVolume), lifetime.Token).ConfigureAwait(false);
                    await engine.SetAsync("speed", new MpvValue.Number(desiredRate), lifetime.Token).ConfigureAwait(false);
                    await engine.SetAsync("mute", new MpvValue.Flag(desiredMuted), lifetime.Token).ConfigureAwait(false);
                }
                Update(snapshot with { Volume = desiredVolume, PlaybackRate = desiredRate, IsMuted = desiredMuted });
                TrackPreparation(LoadSubtitlesAsync(current));
                break;
            case EngineEvent.PlaybackRestart when active is { Loaded: true, Ended: false } started && !closing:
                if (!started.Confirmed)
                {
                    started.Confirmed = true; hasConfirmedPlayback = true; consecutiveSkips = 0;
                    started.StartTimeTicks = (clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks;
                    slowTimer?.Dispose(); slowTimer = null;
                    Update(snapshot with { Phase = PlayerPhase.Playing, IsSlowOpening = false, IsSeeking = false });
                    Observe(reporter.PlayingAsync(started.ReportId, Payload(started)));
                    progressTimer ??= clock.CreateTimer(_ => Post(() => { Progress("TimeUpdate"); return Task.CompletedTask; }),
                        null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                    progressTimer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                    if (appended is null) Prepare(started.Index + 1, 0, append: true);
                }
                else Update(snapshot with { IsSeeking = false });
                break;
            case EngineEvent.EndFile end when loaded.TryGetValue(end.EntryId, out var endedEntry):
                if (end.Reason == EngineEndReason.Redirect) break;
                if (end.Reason == EngineEndReason.Error && !closing && !endedEntry.Ended && !endedEntry.NativeFailureLogged)
                {
                    endedEntry.NativeFailureLogged = true;
                    endedEntry.LastNativeFailure = new(AppErrorKind.Player, ErrorCodes.PlaybackFailed,
                        $"片源打开失败（阶段 playback.candidate.end-file，候选 {(endedEntry.CandidateIndex + 1).ToString(CultureInfo.InvariantCulture)}，原生错误 {end.Error.ToString(CultureInfo.InvariantCulture)}）。",
                        true, "playback.candidate.end-file", end.Error, Guid.NewGuid().ToString("N"));
                    log?.Invoke(endedEntry.LastNativeFailure);
                }
                if (!endedEntry.Confirmed && end.Reason == EngineEndReason.Error && !closing)
                {
                    loaded.Remove(end.EntryId);
                    if (active == endedEntry) active = null;
                    if (appended == endedEntry) appended = null;
                    await ResolveAsync(endedEntry, endedEntry.CandidateIndex + 1, append: false).ConfigureAwait(false);
                    break;
                }
                Stop(endedEntry);
                if (closing) { await FinalizeCloseAsync().ConfigureAwait(false); break; }
                if (active != endedEntry) break;
                if (switching || appended is not null) { Update(snapshot with { Phase = PlayerPhase.Interstitial }); break; }
                if (end.Reason == EngineEndReason.Eof)
                {
                    if (endedEntry.Index + 1 < snapshot.Entries.Length)
                    { Update(snapshot with { Phase = PlayerPhase.Interstitial }); Prepare(endedEntry.Index + 1, 0, false); }
                    else await BeginCloseAsync(PlaybackEndReason.SeasonEnded).ConfigureAwait(false);
                }
                else if (end.Reason == EngineEndReason.Error)
                    Fail(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed,
                        $"播放意外中断，已保存最新进度。（错误代码：{end.Error.ToString(CultureInfo.InvariantCulture)}）",
                        true, "playback.end-file", end.Error, endedEntry.LastNativeFailure?.DiagnosticId));
                break;
            case EngineEvent.PropertyChanged property when !closing:
                PropertyChanged(property);
                break;
            case EngineEvent.Shutdown when !closing:
                if (active is { } interrupted) Stop(interrupted);
                Fail(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "播放器已退出。", true));
                break;
            case EngineEvent.QueueOverflow when !closing:
                // 引擎随后补发关键属性快照，继续消费；不能制造一次虚假的停止上报。
                break;
            case EngineEvent.Failure when !closing:
                if (active is { } failed) Stop(failed);
                Fail(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "播放器事件处理失败，请重试。", true));
                break;
        }
    }

    private void PreparationFailed(PlaybackEntry entry, int index, bool append, AppError error)
    {
        preparing.Remove(index);
        switching = false;
        if (append || hasConfirmedPlayback)
        {
            scheduler.TryEnqueue(() => { if (!finalized) skipped(entry, error); });
            if (++consecutiveSkips <= 3 && index + 1 < snapshot.Entries.Length)
            { Prepare(index + 1, 0, append && active is { Ended: false }); return; }
            if (active is { Confirmed: true, Ended: false }) return;
        }
        Fail(error);
    }
    private async Task LoadSubtitlesAsync(LoadedEntry entry)
    {
        if (entry.Candidate is null) return;
        ImmutableArray<ResolvedSubtitle> subtitles = [];
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        try
        {
            subtitles = await preparer.ResolveSubtitlesAsync(account, entry.Prepared, entry.Candidate, preparation.Token).ConfigureAwait(false);
            var ready = subtitles;
            if (!Post(async () =>
            {
                if (closing || active != entry || entry.Ended) { await preparer.ReleaseSubtitlesAsync(ready).ConfigureAwait(false); return; }
                entry.Subtitles = ready;
                foreach (var subtitle in ready)
                    if (engine is not null)
                        await engine.CommandAsync(new[] { "sub-add", subtitle.LocalPath, "auto", subtitle.Subtitle.Title,
                            subtitle.Subtitle.Language }, lifetime.Token).ConfigureAwait(false);
            })) await preparer.ReleaseSubtitlesAsync(ready).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await preparer.ReleaseSubtitlesAsync(subtitles).ConfigureAwait(false);
            if (!preparation.IsCancellationRequested) log?.Invoke(SafeError(exception));
        }
    }

    private PlaybackReport Payload(LoadedEntry entry, string? eventName = null) => new()
    {
        ItemId = entry.Prepared.Entry.ItemId, MediaSourceId = entry.Candidate?.Candidate.MediaSource.Id,
        LiveStreamId = entry.Candidate?.Candidate.MediaSource.LiveStreamId, PlaySessionId = entry.Prepared.PlaybackInfo.PlaySessionId,
        PlayMethod = entry.Candidate?.Candidate.PlayMethod ?? "DirectPlay", PositionTicks = entry.PositionTicks,
        PlaybackStartTimeTicks = entry.StartTimeTicks, IsPaused = snapshot.IsPaused, IsMuted = snapshot.IsMuted,
        VolumeLevel = snapshot.Volume, PlaybackRate = snapshot.PlaybackRate, EventName = eventName,
        PlaylistIndex = entry.Index, PlaylistLength = snapshot.Entries.Length,
        AudioStreamIndex = TrackIndex(snapshot.SelectedAudioTrackId, TrackKind.Audio, entry),
        SubtitleStreamIndex = TrackIndex(snapshot.SelectedSubtitleTrackId, TrackKind.Subtitle, entry),
    };
    private static int? TrackIndex(string? id, TrackKind kind, LoadedEntry entry)
        => id is not null && entry.TrackIndexes.TryGetValue((kind, id), out var index) ? index : null;
    private void Stop(LoadedEntry entry)
    {
        if (entry.Ended) return;
        entry.Ended = true;
        if (entry.Confirmed)
        {
            lastStop = Task.WhenAll(lastStop, reporter.StoppedAsync(entry.ReportId, entry.Prepared.Entry, Payload(entry),
                entry.Candidate?.Candidate.PlayMethod == "Transcode",
                closeReason == PlaybackEndReason.AppShutdown ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(3)));
            Observe(lastStop);
        }
        else if ((entry.Candidate?.Candidate.PlayMethod == "Transcode" ||
            entry.Candidate is null && entry.CandidateIndex < entry.Prepared.Candidates.Length && entry.Prepared.Candidates[entry.CandidateIndex].PlayMethod == "Transcode") &&
            entry.Prepared.PlaybackInfo.PlaySessionId is { Length: > 0 } sessionId)
        {
            lastStop = Task.WhenAll(lastStop, reporter.CleanupAsync(sessionId, TimeSpan.FromSeconds(1.5)));
            Observe(lastStop);
        }
        if (!entry.Subtitles.IsEmpty) background.Add(preparer.ReleaseSubtitlesAsync(entry.Subtitles));
    }
    private void Progress(string eventName)
    {
        if (!closing && active is { Confirmed: true, Ended: false } current)
            Observe(reporter.ProgressAsync(current.ReportId, Payload(current, eventName)));
    }
    private void PropertyChanged(EngineEvent.PropertyChanged change)
    {
        var value = change.Value;
        double? number = value switch { MpvValue.Number item => item.Value, MpvValue.WholeNumber item => item.Value, _ => null };
        bool? flag = value is MpvValue.Flag boolean ? boolean.Value : null;
        switch (change.Property)
        {
            case EngineProperty.TimePosition when number is { } seconds && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds:
                var ticks = TimeSpan.FromSeconds(seconds).Ticks;
                if (active is { Ended: false } current) current.PositionTicks = ticks;
                Update(snapshot with { PositionTicks = ticks }, throttle: true); break;
            case EngineProperty.Duration when number is { } duration && double.IsFinite(duration) && duration >= 0 && duration < TimeSpan.MaxValue.TotalSeconds:
                Update(snapshot with { DurationTicks = TimeSpan.FromSeconds(duration).Ticks }); break;
            case EngineProperty.Pause when flag is { } pause: Update(snapshot with { IsPaused = pause }); break;
            case EngineProperty.PausedForCache when flag is { } buffering: Update(snapshot with { IsBuffering = buffering }); break;
            case EngineProperty.Seeking when flag is { } seeking: Update(snapshot with { IsSeeking = seeking }); break;
            case EngineProperty.Speed when active is { Loaded: true } && number is { } speed && double.IsFinite(speed) && speed > 0:
                Update(snapshot with { PlaybackRate = speed }); break;
            case EngineProperty.Volume when active is { Loaded: true } && number is { } volume && double.IsFinite(volume):
                Update(snapshot with { Volume = Math.Clamp(volume, 0, 100) }); break;
            case EngineProperty.Mute when active is { Loaded: true } && flag is { } mute: Update(snapshot with { IsMuted = mute }); break;
            case EngineProperty.AudioTrack:
                Update(snapshot with { SelectedAudioTrackId = TrackId(value) }); break;
            case EngineProperty.SubtitleTrack:
                Update(snapshot with { SelectedSubtitleTrackId = TrackId(value) }); break;
            case EngineProperty.TrackList when value is MpvValue.Array tracks:
                Tracks(tracks); break;
            case EngineProperty.DemuxerCacheState when value is MpvValue.Map cache:
                var ranges = ImmutableArray.CreateBuilder<BufferedRange>();
                if (cache.Values.GetValueOrDefault("seekable-ranges") is MpvValue.Array buffered)
                    foreach (var item in buffered.Values.Take(64).OfType<MpvValue.Map>())
                    {
                        var start = Numeric(item.Values.GetValueOrDefault("start"));
                        var end = Numeric(item.Values.GetValueOrDefault("end"));
                        if (start is >= 0 && end >= start && end < TimeSpan.MaxValue.TotalSeconds)
                            ranges.Add(new(TimeSpan.FromSeconds(start.Value).Ticks, TimeSpan.FromSeconds(end.Value).Ticks));
                    }
                Update(snapshot with { BufferedRanges = ranges.ToImmutable() }); break;
        }
    }
    private void Tracks(MpvValue.Array values)
    {
        var audio = ImmutableArray.CreateBuilder<TrackInfo>();
        var subtitles = ImmutableArray.CreateBuilder<TrackInfo>();
        active?.TrackIndexes.Clear();
        foreach (var track in values.Values.OfType<MpvValue.Map>())
        {
            var type = Text(track.Values.GetValueOrDefault("type"));
            var kind = type == "audio" ? TrackKind.Audio : TrackKind.Subtitle;
            if (type is not ("audio" or "sub")) continue;
            var list = kind == TrackKind.Audio ? audio : subtitles;
            if (list.Count >= 32) continue;
            var id = TrackId(track.Values.GetValueOrDefault("id"));
            if (id is null) continue;
            var language = CleanLabel(Text(track.Values.GetValueOrDefault("lang")));
            var title = CleanLabel(Text(track.Values.GetValueOrDefault("title")));
            var label = string.Join(" · ", new[] { title, language }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (string.IsNullOrEmpty(label)) label = (kind == TrackKind.Audio ? "音轨 " : "字幕 ") + (list.Count + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new(id, kind, label.Length > 96 ? label[..96] : label) { Language = language,
                IsDefault = track.Values.GetValueOrDefault("default") is MpvValue.Flag { Value: true } });
            if (Numeric(track.Values.GetValueOrDefault("ff-index")) is { } index && index >= 0 && index <= int.MaxValue && active is { } entry)
            {
                var mediaType = kind == TrackKind.Audio ? "Audio" : "Subtitle";
                if (entry.Candidate?.Candidate.MediaSource.MediaStreams?.Any(stream => stream.Index == (int)index && stream.Type == mediaType) == true)
                    entry.TrackIndexes[(kind, id)] = (int)index;
            }
        }
        Update(snapshot with { AudioTracks = audio.ToImmutable(), SubtitleTracks = subtitles.ToImmutable() });
    }
    private static string? CleanLabel(string? value)
    {
        if (value is null || value.Contains("://", StringComparison.Ordinal) || value.Contains('\\') || value.StartsWith('/')) return null;
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(96).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
    private static double? Numeric(MpvValue? value) => value switch
    { MpvValue.Number item when double.IsFinite(item.Value) => item.Value, MpvValue.WholeNumber item => item.Value, _ => null };
    private static string? Text(MpvValue? value) => value is MpvValue.Text text ? text.Value : null;
    private static string? TrackId(MpvValue? value) => value switch
    { MpvValue.Text text when text.Value != "no" => text.Value, MpvValue.WholeNumber item => item.Value.ToString(CultureInfo.InvariantCulture), _ => null };
    private void Observe(Task task) => _ = ObserveAsync(task);
    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { log?.Invoke(SafeError(exception)); }
    }
    private void Fail(AppError error)
    {
        log?.Invoke(error);
        Update(snapshot with { Phase = PlayerPhase.Failed, Error = error, IsSlowOpening = false });
    }
    private void Update(SessionSnapshot value, bool throttle = false)
    {
        Volatile.Write(ref snapshot, value with { CapturedAtUtc = clock.GetUtcNow() });
        if (throttle)
        {
            notificationTimer ??= clock.CreateTimer(_ => QueueNotification(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Interlocked.Exchange(ref throttleScheduled, 1) == 0)
                notificationTimer.Change(TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        }
        else QueueNotification();
    }
    private void QueueNotification()
    {
        Interlocked.Exchange(ref throttleScheduled, 0);
        if (Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
        if (!scheduler.TryEnqueue(() => { Interlocked.Exchange(ref notificationQueued, 0); SnapshotChanged?.Invoke(this, EventArgs.Empty); }))
            Interlocked.Exchange(ref notificationQueued, 0);
    }

    public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(PlaybackEndReason.UserClosed, cancellationToken);
    public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Post(() => BeginCloseAsync(reason));
        return closed.Task.WaitAsync(cancellationToken);
    }
    private async Task BeginCloseAsync(PlaybackEndReason reason)
    {
        if (closing) return;
        closing = true; closeReason = reason; generation++;
        if (reason == PlaybackEndReason.AppShutdown) reporter.BeginShutdown(TimeSpan.FromSeconds(1.5));
        slowTimer?.Dispose(); progressTimer?.Dispose();
        Update(snapshot with { Phase = PlayerPhase.Closing, IsSlowOpening = false });
        if (engine is not null && active is { Ended: false })
        {
            try { await engine.CommandAsync(StopCommand, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { log?.Invoke(SafeError(exception)); }
            closeTimer = clock.CreateTimer(_ => Post(FinalizeCloseAsync), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        else await FinalizeCloseAsync().ConfigureAwait(false);
    }
    private async Task FinalizeCloseAsync()
    {
        if (finalized) return;
        finalized = true; closeTimer?.Dispose();
        notificationTimer?.Dispose();
        lifetime.Cancel();
        try { await Task.WhenAll(preparationWork.ToArray()).ConfigureAwait(false); }
        catch (Exception exception) { log?.Invoke(SafeError(exception)); }
        if (active is { } current) Stop(current);
        foreach (var prepared in preparedEntries) Stop(prepared);
        Exception? finalError = null;
        try { await lastStop.ConfigureAwait(false); }
        catch (Exception exception) { finalError = exception; log?.Invoke(SafeError(exception)); }
        try
        {
            if (Detaching is { } detach)
                foreach (Func<Task> subscriber in detach.GetInvocationList()) await subscriber().ConfigureAwait(false);
            if (engine is { } owner) { engine = null; await owner.DisposeAsync().ConfigureAwait(false); }
            await Task.WhenAll(background.ToArray()).ConfigureAwait(false);
            await reporter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { finalError ??= exception; log?.Invoke(SafeError(exception)); }
        Update(snapshot with { Phase = PlayerPhase.Closed, IsBuffering = false, IsSeeking = false });
        scheduler.TryEnqueue(() => EngineChanged?.Invoke(null));
        ended(this, closeReason);
        inbox.Writer.TryComplete();
        lifetime.Dispose();
        closeError = finalError;
    }

    public Task TogglePauseAsync(CancellationToken cancellationToken = default) => Command(async () =>
    { var pause = !snapshot.IsPaused; await SetAsync("pause", new MpvValue.Flag(pause)).ConfigureAwait(false);
      Update(snapshot with { IsPaused = pause }); Progress(pause ? "Pause" : "Unpause"); }, cancellationToken);
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Command(async () =>
    {
        if (position < TimeSpan.Zero) throw InvalidCommand();
        await EngineRequired.CommandAsync(new[] { "seek", position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture), "absolute" }, lifetime.Token).ConfigureAwait(false);
        if (active is { } current) current.PositionTicks = position.Ticks;
        Update(snapshot with { PositionTicks = position.Ticks, IsSeeking = true }); Progress("TimeUpdate");
    }, cancellationToken);
    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Command(async () =>
    { if (!double.IsFinite(rate) || rate is < 0.25 or > 4) throw InvalidCommand();
      await SetAsync("speed", new MpvValue.Number(rate)).ConfigureAwait(false); desiredRate = rate;
      Update(snapshot with { PlaybackRate = rate }); }, cancellationToken);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Command(async () =>
    { if (!double.IsFinite(volume) || volume is < 0 or > 100) throw InvalidCommand();
      if (engine is not null) await SetAsync("volume", new MpvValue.Number(volume)).ConfigureAwait(false);
      desiredVolume = volume; Update(snapshot with { Volume = volume }); }, cancellationToken);
    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Command(async () =>
    { await SetAsync("mute", new MpvValue.Flag(muted)).ConfigureAwait(false); desiredMuted = muted;
      Update(snapshot with { IsMuted = muted }); }, cancellationToken);
    private async Task SetAsync(string name, MpvValue value) => await EngineRequired.SetAsync(name, value, lifetime.Token).ConfigureAwait(false);
    private IPlayerEngine EngineRequired => engine ?? throw new AppException(new(AppErrorKind.Player, ErrorCodes.PlaybackBusy, "播放器尚未准备好。", true));
    public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) => SelectTrackAsync(trackId, true, cancellationToken);
    public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) => SelectTrackAsync(trackId, false, cancellationToken);
    private Task SelectTrackAsync(string? id, bool audio, CancellationToken cancellationToken) => Command(async () =>
    {
        var tracks = audio ? snapshot.AudioTracks : snapshot.SubtitleTracks;
        if (id is not null && !tracks.Any(track => track.Id == id)) throw InvalidCommand();
        await SetAsync(audio ? "aid" : "sid", new MpvValue.Text(id ?? "no")).ConfigureAwait(false);
        Update(audio ? snapshot with { SelectedAudioTrackId = id } : snapshot with { SelectedSubtitleTrackId = id });
    }, cancellationToken);
    public Task PreviousAsync(CancellationToken cancellationToken = default) => Command(() => SwitchAsync(snapshot.CurrentEntryIndex - 1), cancellationToken);
    public Task NextAsync(CancellationToken cancellationToken = default) => Command(() => SwitchAsync(snapshot.CurrentEntryIndex + 1), cancellationToken);
    public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default) => Command(() =>
    { var index = -1; for (var i = 0; i < snapshot.Entries.Length; i++) if (snapshot.Entries[i].ItemId == itemId) { index = i; break; }
      return SwitchAsync(index); }, cancellationToken);
    private async Task SwitchAsync(int index)
    {
        if (index < 0 || index >= snapshot.Entries.Length) throw InvalidCommand();
        if (index == snapshot.CurrentEntryIndex) return;
        if (appended is { } next && next.Index == index)
        { switching = true; await EngineRequired.CommandAsync(NextCommand, lifetime.Token).ConfigureAwait(false); }
        else { generation++; appended = null; Prepare(index, 0, false); }
    }
    public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default) => Command(async () =>
    { if (!Enum.IsDefined(direction)) throw InvalidCommand(); await EngineRequired.CommandAsync(new[] {
        direction == FrameStepDirection.Forward ? "frame-step" : "frame-back-step" }, lifetime.Token).ConfigureAwait(false); }, cancellationToken);
    public Task RetryAsync(CancellationToken cancellationToken = default) => Command(async () =>
    {
        if (snapshot.Phase != PlayerPhase.Failed) return;
        generation++;
        foreach (var prepared in preparedEntries) Stop(prepared);
        try { await lastStop.ConfigureAwait(false); }
        catch (Exception exception) { log?.Invoke(SafeError(exception)); }
        lastStop = Task.CompletedTask;
        if (engine is { } old)
        {
            if (Detaching is { } detach) foreach (Func<Task> subscriber in detach.GetInvocationList()) await subscriber().ConfigureAwait(false);
            engine = null; await old.DisposeAsync().ConfigureAwait(false);
        }
        retryRequest = snapshot.Entry is { } retry && !snapshot.Entries.IsEmpty ? new(retry.ItemId, snapshot.PositionTicks) : request;
        loaded.Clear(); preparing.Clear(); preparedEntries.Clear(); active = appended = null; switching = false;
        Initialize();
    }, cancellationToken);
    public async ValueTask DisposeAsync() => await CloseAsync(PlaybackEndReason.AppShutdown).ConfigureAwait(false);
    private void TrackPreparation(Task work) { preparationWork.Add(work); background.Add(work); }
    private static AppException InvalidCommand() => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "播放操作参数无效。", false));
    internal static AppError SafeError(Exception exception) => exception is AppException safe ? safe.Error
        : new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, exception is OperationCanceledException ? "播放准备已取消。" : "播放失败，请重试。", true);

    private sealed record Input(Func<Task> Action, TaskCompletionSource? Completion = null);
    private sealed class LoadedEntry(PreparedEntry prepared, int index)
    {
        public PreparedEntry Prepared { get; } = prepared;
        public int Index { get; } = index;
        public string ReportId { get; } = Guid.NewGuid().ToString("D");
        public int CandidateIndex { get; set; }
        public bool NativeFailureLogged { get; set; }
        public AppError? LastNativeFailure { get; set; }
        public ResolvedCandidate? Candidate { get; set; }
        public long NativeId { get; set; }
        public bool Loaded { get; set; }
        public bool WasAppended { get; set; }
        public bool Confirmed { get; set; }
        public bool Ended { get; set; }
        public long PositionTicks { get; set; }
        public long StartTimeTicks { get; set; }
        public Dictionary<(TrackKind, string), int> TrackIndexes { get; } = [];
        public ImmutableArray<ResolvedSubtitle> Subtitles { get; set; } = [];
    }
}
