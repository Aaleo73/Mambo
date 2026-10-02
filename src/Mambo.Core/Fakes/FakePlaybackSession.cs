using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>Deterministic managed playback state, including transitions for UI debugging.</summary>
public sealed class FakePlaybackSession : IPlaybackSession, IDisposable, IAsyncDisposable
{
    private readonly FakeOperation operation;
    private readonly FakeOptions options;
    private readonly TimeProvider clock;
    private readonly IUiScheduler scheduler;
    private readonly IMessenger messenger;
    private readonly Action<FakePlaybackSession, PlaybackEndReason> onClosed;
    private readonly Action<PlaybackEntry, AppError> onSkipped;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ITimer timer;
    private SessionSnapshot snapshot;
    private long lastTimestamp;
    private TimeSpan playSinceBuffer;
    private DateTimeOffset? bufferUntil;
    private DateTimeOffset openingStartedUtc;
    private int transition;
    private int notificationEpoch;
    private bool confirmed;
    private bool prepared;
    private readonly Func<(ImmutableArray<PlaybackEntry> Entries, int Index, long Start)>? prepare;
    private bool stopped;
    private bool closeCompleted;
    private Task? closeTask;

    internal FakePlaybackSession(ImmutableArray<PlaybackEntry> entries, int entryIndex, long startTicks,
        FakeOperation operation, FakeOptions options, TimeProvider clock, IUiScheduler scheduler,
        IMessenger messenger, Action<FakePlaybackSession, PlaybackEndReason> onClosed, Action<PlaybackEntry, AppError> onSkipped, Func<(ImmutableArray<PlaybackEntry> Entries, int Index, long Start)>? prepare = null)
    {
        if (options.PlaybackTick <= TimeSpan.Zero || options.BufferEvery < TimeSpan.Zero || options.BufferDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "演示播放计时设置无效。");
        this.operation = operation;
        this.options = options;
        this.clock = clock;
        this.scheduler = scheduler;
        this.messenger = messenger;
        this.onClosed = onClosed;
        this.onSkipped = onSkipped;
        this.prepare = prepare;
        var entry = entries[entryIndex];
        var duration = Math.Max(1, entry.DurationTicks ?? TimeSpan.FromMinutes(24).Ticks);
        snapshot = new SessionSnapshot
        {
            Phase = PlayerPhase.Preparing,
            EngineKind = EngineKind.Demo,
            Entries = entries,
            Entry = entry,
            CurrentEntryIndex = entryIndex,
            DurationTicks = duration,
            PositionTicks = Math.Clamp(startTicks, 0, duration),
            AudioTracks = [new("audio-1", TrackKind.Audio, "原声 · 中文") { Language = "zho", IsDefault = true },
                new("audio-2", TrackKind.Audio, "配音 · 英语") { Language = "eng" }],
            SubtitleTracks = [new("subtitle-1", TrackKind.Subtitle, "简体中文 · 中文") { Language = "zho", IsDefault = true },
                new("subtitle-2", TrackKind.Subtitle, "English · 英语") { Language = "eng" }],
            SelectedAudioTrackId = "audio-1",
            SelectedSubtitleTrackId = "subtitle-1",
            CapturedAtUtc = clock.GetUtcNow(),
            DemoColorArgb = ColorFor(entry.ItemId),
        };
        lastTimestamp = clock.GetTimestamp();
        openingStartedUtc = clock.GetUtcNow();
        timer = clock.CreateTimer(Tick, null, options.PlaybackTick, options.PlaybackTick);
    }

    public SessionSnapshot Snapshot { get { lock (gate) return snapshot; } }
    internal bool IsClosed => Volatile.Read(ref stopped);
    internal string? ItemId => Volatile.Read(ref snapshot).Entry?.ItemId;
    public event EventHandler? SnapshotChanged;

    internal void Start() => _ = PrepareAsync();

    private async Task PrepareAsync()
    {
        await Task.Yield();
        try
        {
            var result = prepare?.Invoke();
            lock (gate)
            {
                if (stopped) return;
                if (result is { } data) snapshot = snapshot with { Entries = data.Entries, CurrentEntryIndex = data.Index, Entry = data.Entries[data.Index],
                    PositionTicks = data.Start, DurationTicks = data.Entries[data.Index].DurationTicks ?? TimeSpan.FromMinutes(24).Ticks, DemoColorArgb = ColorFor(data.Entries[data.Index].ItemId) };
                prepared = true;
                snapshot = snapshot with { Phase = PlayerPhase.Opening };
                Publish();
            }
            await OpenAsync(0).ConfigureAwait(false);
        }
        catch (AppException error)
        {
            lock (gate) { if (stopped) return; snapshot = snapshot with { Phase = PlayerPhase.Failed, Error = error.Error }; Publish(); }
        }
    }

    private async Task OpenAsync(int expectedTransition)
    {
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (stopped) return;
            cancellationToken = lifetime.Token;
        }
        try
        {
            await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (stopped || expectedTransition != transition) return;
                lastTimestamp = clock.GetTimestamp();
                confirmed = true;
                snapshot = snapshot with { Phase = PlayerPhase.Playing, Error = null, IsSlowOpening = false,
                    CapturedAtUtc = clock.GetUtcNow(), BufferedRanges = Buffer(snapshot.PositionTicks, snapshot.DurationTicks) };
                Publish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (AppException error)
        {
            lock (gate)
            {
                if (stopped || expectedTransition != transition) return;
                snapshot = snapshot with { Phase = PlayerPhase.Failed, Error = error.Error, IsBuffering = false,
                    CapturedAtUtc = clock.GetUtcNow() };
                Publish();
            }
        }
    }

    private void Tick(object? state)
    {
        lock (gate)
        {
            if (stopped) return;
            var now = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(lastTimestamp, now);
            lastTimestamp = now;
            if (snapshot.Phase is PlayerPhase.Opening or PlayerPhase.Interstitial)
            {
                if (!snapshot.IsSlowOpening && clock.GetUtcNow() - openingStartedUtc >= TimeSpan.FromSeconds(20))
                {
                    snapshot = snapshot with { IsSlowOpening = true };
                    Publish();
                }
                return;
            }
            if (snapshot.Phase != PlayerPhase.Playing) return;
            if (snapshot.IsBuffering)
            {
                if (bufferUntil is not null && clock.GetUtcNow() >= bufferUntil)
                {
                    bufferUntil = null;
                    snapshot = snapshot with { IsBuffering = false, CapturedAtUtc = clock.GetUtcNow() };
                    Publish();
                }
                return;
            }
            if (snapshot.IsPaused) return;
            var advance = elapsed.Ticks * snapshot.PlaybackRate;
            var position = snapshot.PositionTicks + (long)Math.Min(advance, long.MaxValue - snapshot.PositionTicks);
            playSinceBuffer += elapsed;
            if (position >= snapshot.DurationTicks)
            {
                snapshot = snapshot with { PositionTicks = snapshot.DurationTicks, CapturedAtUtc = clock.GetUtcNow() };
                if (snapshot.CanNext) SwitchEntry(snapshot.CurrentEntryIndex + 1, PlayerPhase.Interstitial);
                else _ = CloseAsync(PlaybackEndReason.SeasonEnded);
                return;
            }
            var buffering = options.BufferEvery > TimeSpan.Zero && options.BufferDuration > TimeSpan.Zero && playSinceBuffer >= options.BufferEvery;
            if (buffering) { playSinceBuffer = TimeSpan.Zero; bufferUntil = clock.GetUtcNow() + options.BufferDuration; }
            snapshot = snapshot with { PositionTicks = position, IsBuffering = buffering, CapturedAtUtc = clock.GetUtcNow(),
                BufferedRanges = Buffer(position, snapshot.DurationTicks) };
            Publish();
        }
    }

    public Task TogglePauseAsync(CancellationToken cancellationToken = default) => ChangePlaying(current =>
        current with { IsPaused = !current.IsPaused }, cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero) throw Error("demo.playback.position", "跳转位置不能小于零。");
        return ChangePlaying(current => current with { PositionTicks = Math.Min(position.Ticks, current.DurationTicks),
            BufferedRanges = Buffer(Math.Min(position.Ticks, current.DurationTicks), current.DurationTicks) }, cancellationToken);
    }

    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(rate) || rate is < 0.25 or > 4)
            throw Error("demo.playback.rate", "倍速必须介于 0.25 和 4 之间。");
        return ChangeActive(current => current with { PlaybackRate = rate }, cancellationToken);
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
            throw Error("demo.playback.volume", "音量必须介于 0 和 100 之间。");
        return ChangeActive(current => current with { Volume = volume }, cancellationToken);
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        ChangeActive(current => current with { IsMuted = muted }, cancellationToken);

    public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) =>
        ChangeActive(current =>
        {
            if (trackId is not null && !current.AudioTracks.Any(track => track.Id == trackId))
                throw Error("demo.playback.track", "未找到这个音轨。");
            return current with { SelectedAudioTrackId = trackId };
        }, cancellationToken);

    public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) =>
        ChangeActive(current =>
        {
            if (trackId is not null && !current.SubtitleTracks.Any(track => track.Id == trackId))
                throw Error("demo.playback.track", "未找到这个字幕轨道。");
            return current with { SelectedSubtitleTrackId = trackId };
        }, cancellationToken);

    public Task PreviousAsync(CancellationToken cancellationToken = default) => Move(-1, cancellationToken);
    public Task NextAsync(CancellationToken cancellationToken = default) => Move(1, cancellationToken);

    private Task Move(int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            var index = snapshot.CurrentEntryIndex + delta;
            if (index < 0 || index >= snapshot.Entries.Length)
                throw Error("demo.playback.end", delta < 0 ? "已经是第一集。" : "已经是最后一集。");
            SwitchEntry(index, PlayerPhase.Opening);
        }
        return Task.CompletedTask;
    }

    public Task SelectEntryAsync(string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            var index = -1;
            for (var i = 0; i < snapshot.Entries.Length; i++)
                if (snapshot.Entries[i].ItemId == itemId) { index = i; break; }
            if (index < 0) throw Error("demo.playback.entry", "这集不在当前连播列表中。");
            if (index != snapshot.CurrentEntryIndex) SwitchEntry(index, PlayerPhase.Opening);
        }
        return Task.CompletedTask;
    }

    private void SwitchEntry(int index, PlayerPhase phase)
    {
        SendStopped(snapshot.Entry);
        confirmed = false;
        transition++;
        var entry = snapshot.Entries[index];
        playSinceBuffer = TimeSpan.Zero;
        bufferUntil = null;
        lastTimestamp = clock.GetTimestamp();
        openingStartedUtc = clock.GetUtcNow();
        snapshot = snapshot with { Phase = phase, Entry = entry, CurrentEntryIndex = index, PositionTicks = 0,
            DurationTicks = Math.Max(1, entry.DurationTicks ?? TimeSpan.FromMinutes(24).Ticks), IsPaused = false,
            IsBuffering = false, IsSeeking = false, IsSlowOpening = false, Error = null, BufferedRanges = [],
            CapturedAtUtc = clock.GetUtcNow(), DemoColorArgb = ColorFor(entry.ItemId) };
        Publish();
        _ = OpenAsync(transition);
    }

    public Task StepFrameAsync(FrameStepDirection direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (FrameStepDirection.Forward or FrameStepDirection.Backward))
            throw Error("demo.playback.frame", "逐帧方向无效。");
        return ChangePlaying(current =>
        {
            var step = TimeSpan.TicksPerSecond / 24 * (direction == FrameStepDirection.Forward ? 1 : -1);
            var position = Math.Clamp(current.PositionTicks + step, 0, current.DurationTicks);
            return current with { IsPaused = true, PositionTicks = position, BufferedRanges = Buffer(position, current.DurationTicks) };
        }, cancellationToken);
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            if (snapshot.Phase != PlayerPhase.Failed) return Task.CompletedTask;
            if (!prepared) { snapshot = snapshot with { Phase = PlayerPhase.Preparing, Error = null }; Publish(); _ = PrepareAsync(); return Task.CompletedTask; }
            transition++;
            openingStartedUtc = clock.GetUtcNow();
            snapshot = snapshot with { Phase = PlayerPhase.Opening, Error = null, IsBuffering = false,
                IsSlowOpening = false, CapturedAtUtc = clock.GetUtcNow() };
            Publish();
            _ = OpenAsync(transition);
        }
        return Task.CompletedTask;
    }

    /// <summary>Debug-only fake control; buffering does not advance playback time.</summary>
    public Task SimulateBufferingAsync(bool buffering, CancellationToken cancellationToken = default)
    {
        lock (gate) bufferUntil = null;
        return ChangePlaying(current => current with { IsBuffering = buffering }, cancellationToken);
    }

    /// <summary>Debug-only fake control; Retry resumes from the current position.</summary>
    public Task SimulateFailureAsync(AppError? error = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            AdvanceForCommand();
            transition++;
            bufferUntil = null;
            snapshot = snapshot with { Phase = PlayerPhase.Failed, IsBuffering = false,
                Error = error ?? new AppError(AppErrorKind.Player, "demo.playback.failed", "演示播放中断，请重试。", true),
                CapturedAtUtc = clock.GetUtcNow() };
            Publish();
        }
        return Task.CompletedTask;
    }

    /// <summary>Exercises the frontend's skipped-entry toast without a network or player dependency.</summary>
    public Task SimulateNextEntryFailureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            if (!snapshot.CanNext) throw Error("demo.playback.end", "已经是最后一集。");
            var entry = snapshot.Entries[snapshot.CurrentEntryIndex + 1];
            var error = new AppError(AppErrorKind.Player, "demo.playback.skipped", "有一集无法加入连播，已跳过。", true);
            Enqueue(() => onSkipped(entry, error), retainUntilClose: true);
            var after = snapshot.CurrentEntryIndex + 2;
            if (after < snapshot.Entries.Length) SwitchEntry(after, PlayerPhase.Interstitial);
            else _ = CloseAsync(CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    private Task ChangePlaying(Func<SessionSnapshot, SessionSnapshot> change, CancellationToken cancellationToken) =>
        ChangeActive(current => current.Phase == PlayerPhase.Playing ? change(current) :
            throw Error("demo.playback.not-playing", "请等待演示画面打开后再操作。"), cancellationToken);

    private Task ChangeActive(Func<SessionSnapshot, SessionSnapshot> change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureActive();
            AdvanceForCommand();
            snapshot = change(snapshot) with { CapturedAtUtc = clock.GetUtcNow() };
            Publish();
        }
        return Task.CompletedTask;
    }

    private void AdvanceForCommand()
    {
        var now = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(lastTimestamp, now);
        lastTimestamp = now;
        if (snapshot.Phase != PlayerPhase.Playing || snapshot.IsPaused || snapshot.IsBuffering) return;
        var advance = elapsed.Ticks * snapshot.PlaybackRate;
        var position = Math.Min(snapshot.DurationTicks,
            snapshot.PositionTicks + (long)Math.Min(advance, long.MaxValue - snapshot.PositionTicks));
        playSinceBuffer += elapsed;
        snapshot = snapshot with { PositionTicks = position, BufferedRanges = Buffer(position, snapshot.DurationTicks) };
    }

    private void EnsureActive()
    {
        if (stopped) throw Error(ErrorCodes.SessionClosed, "播放已经结束。", AppErrorKind.Cancelled);
    }

    public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(PlaybackEndReason.UserClosed, cancellationToken);

    public Task CloseAsync(PlaybackEndReason reason, CancellationToken cancellationToken = default)
    {
        Task cleanup;
        lock (gate)
        {
            if (closeTask is not null) cleanup = closeTask;
            else if (closeCompleted) cleanup = Task.CompletedTask;
            else
            {
                Volatile.Write(ref stopped, true);
                transition++;
                notificationEpoch++;
                timer.Dispose();
                lifetime.Cancel();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                closeTask = cleanup = completion.Task;
                snapshot = snapshot with { Phase = PlayerPhase.Closed, IsBuffering = false, IsSeeking = false,
                    CapturedAtUtc = clock.GetUtcNow() };
                var entry = confirmed ? snapshot.Entry : null;
                if (!scheduler.TryEnqueue(() =>
                {
                    lock (gate) if (closeCompleted) { completion.TrySetResult(); return; }
                    try
                    {
                        SnapshotChanged?.Invoke(this, EventArgs.Empty);
                        if (entry is not null) messenger.Send(new PlaybackStopped(entry.ItemId, entry.SeriesId, entry.SeasonId));
                        onClosed(this, reason);
                    }
                    finally
                    {
                        lock (gate) closeCompleted = true;
                        lifetime.Dispose();
                        completion.TrySetResult();
                    }
                }))
                {
                    closeCompleted = true;
                    lifetime.Dispose();
                    completion.TrySetResult();
                }
            }
        }
        return cancellationToken.CanBeCanceled ? cleanup.WaitAsync(cancellationToken) : cleanup;
    }

    private void Publish() => Enqueue(() => SnapshotChanged?.Invoke(this, EventArgs.Empty));

    private void SendStopped(PlaybackEntry? entry)
    {
        if (entry is not null && confirmed)
            Enqueue(() => messenger.Send(new PlaybackStopped(entry.ItemId, entry.SeriesId, entry.SeasonId)), retainUntilClose: true);
    }

    private void Enqueue(Action action, bool retainUntilClose = false)
    {
        var epoch = notificationEpoch;
        scheduler.TryEnqueue(() =>
        {
            lock (gate) if (closeCompleted || !retainUntilClose && epoch != notificationEpoch) return;
            action();
        });
    }

    private static ImmutableArray<BufferedRange> Buffer(long position, long duration) =>
        [new(Math.Max(0, position - TimeSpan.FromSeconds(10).Ticks), Math.Min(duration, position + TimeSpan.FromSeconds(90).Ticks))];

    private static uint ColorFor(string itemId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(itemId));
        return 0xFF000000u | (uint)(30 + bytes[0] % 70) << 16 | (uint)(30 + bytes[1] % 70) << 8 | (uint)(30 + bytes[2] % 70);
    }

    private static AppException Error(string code, string message, AppErrorKind kind = AppErrorKind.Player) =>
        new(new AppError(kind, code, message, false));

    public void Dispose()
    {
        lock (gate)
        {
            if (closeCompleted) return;
            Volatile.Write(ref stopped, true);
            closeCompleted = true;
            notificationEpoch++;
            timer.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
            snapshot = snapshot with { Phase = PlayerPhase.Closed, IsBuffering = false, CapturedAtUtc = clock.GetUtcNow() };
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
