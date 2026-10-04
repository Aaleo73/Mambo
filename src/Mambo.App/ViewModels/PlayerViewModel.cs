using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>播放层的纯契约投影。计时器只插值快照，不向播放引擎轮询。</summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackSession session;
    private SessionSnapshot snapshot;
    private long sampleTime;
    private long? seekPreview;
    private long seekHoldUntil;
    private string? entryKey;
    private bool upNextDismissed;
    private long openingSince;
    private bool slowOpeningNotified;
    private bool disposed;

    public PlayerViewModel(IPlaybackSession session)
    {
        this.session = session;
        snapshot = session.Snapshot;
        session.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot();
    }

    internal SessionSnapshot Snapshot => snapshot;
    public ObservableCollection<PlayerEpisodeViewModel> Episodes { get; } = [];
    public bool CanControl => snapshot.Phase == PlayerPhase.Playing;
    public bool IsOpening => snapshot.Phase is PlayerPhase.Preparing or PlayerPhase.Opening or PlayerPhase.Interstitial;
    public bool IsFailed => snapshot.Phase == PlayerPhase.Failed;
    public bool IsExternal => snapshot.EngineKind == EngineKind.External;
    public bool ShowExternalPanel => IsExternal && !IsOpening && !IsFailed;
    public bool IsSlowOpening => IsOpening && (snapshot.IsSlowOpening || openingSince > 0 && Environment.TickCount64 - openingSince >= 20_000);
    public bool IsBuffering => snapshot.IsBuffering && CanControl;
    public bool CanPrevious => CanControl && snapshot.CanPrevious;
    public bool CanNext => CanControl && snapshot.CanNext;
    public bool HasEpisodes => Episodes.Count > 1;
    public bool IsPaused => snapshot.IsPaused && CanControl;
    public string PauseGlyph => (string)Application.Current.Resources[snapshot.IsPaused ? "IconPlay" : "IconPause"];
    public string PauseAccessibleName => snapshot.IsPaused ? "继续播放" : "暂停播放";
    public string MuteAccessibleName => snapshot.IsMuted ? "取消静音" : "静音";
    public string VolumeGlyph => (string)Application.Current.Resources[snapshot.IsMuted || snapshot.Volume == 0 ? "IconVolumeMuted" : "IconVolumeWaves"];
    public string RateText => Math.Abs(snapshot.PlaybackRate - 1) < .001 ? "倍速" : snapshot.PlaybackRate.ToString("0.##", CultureInfo.InvariantCulture) + "×";
    public string DurationText => FormatTicks(snapshot.DurationTicks);
    public double DurationSeconds => Math.Max(1, TimeSpan.FromTicks(Math.Max(0, snapshot.DurationTicks)).TotalSeconds);
    public double Volume => Math.Clamp(snapshot.Volume, 0, 100);
    public string ErrorText => snapshot.Error?.Message ?? "这部片子暂时打不开";
    public string Title => snapshot.Entry is { SeriesName: { Length: > 0 } seriesName } ? seriesName : snapshot.Entry?.Title ?? "播放";
    public string Subtitle => snapshot.Entry is { } entry && !string.IsNullOrEmpty(entry.SeriesName)
        ? $"第 {entry.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} 季 · 第 {entry.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} 集 · {entry.EpisodeName ?? entry.Title}"
        : "";
    public string ShellTitle => string.IsNullOrEmpty(Subtitle) ? Title : Title + " · " + (snapshot.Entry?.EpisodeLabel ?? Subtitle);
    public string NextTitle => snapshot.CanNext ? snapshot.Entries[snapshot.CurrentEntryIndex + 1].Title : "";
    public string RemainingText => FormatTicks(Math.Max(0, snapshot.DurationTicks - DisplayPositionTicks));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    public partial long DisplayPositionTicks { get; private set; }

    public string PositionText => FormatTicks(DisplayPositionTicks);
    public double PositionSeconds => TimeSpan.FromTicks(DisplayPositionTicks).TotalSeconds;

    [ObservableProperty]
    public partial bool ShowUpNext { get; private set; }

    public void Tick()
    {
        if (disposed) return;
        if (IsSlowOpening != slowOpeningNotified)
        {
            slowOpeningNotified = IsSlowOpening;
            OnPropertyChanged(nameof(IsSlowOpening));
        }
        if (seekPreview is not null && seekHoldUntil > 0 && Environment.TickCount64 > seekHoldUntil)
        {
            seekPreview = null;
            seekHoldUntil = 0;
        }
        var ticks = seekPreview ?? snapshot.PositionTicks;
        if (seekPreview is null && CanControl && !snapshot.IsPaused && !snapshot.IsBuffering && !snapshot.IsSeeking)
        {
            var elapsed = Math.Max(0, Environment.TickCount64 - sampleTime);
            ticks += (long)(elapsed * TimeSpan.TicksPerMillisecond * snapshot.PlaybackRate);
        }
        DisplayPositionTicks = ClampPosition(ticks);
        OnPropertyChanged(nameof(PositionSeconds));
        ShowUpNext = CanControl && !upNextDismissed && snapshot.CanNext && snapshot.DurationTicks > 0
            && snapshot.DurationTicks - DisplayPositionTicks is > 0 and <= 20 * TimeSpan.TicksPerSecond;
    }

    public long ClampPosition(long ticks) => snapshot.DurationTicks > 0 ? Math.Clamp(ticks, 0, snapshot.DurationTicks) : Math.Max(0, ticks);

    public void PreviewSeek(double seconds)
    {
        if (disposed) return;
        seekPreview = ClampPosition((long)(Math.Max(0, seconds) * TimeSpan.TicksPerSecond));
        seekHoldUntil = 0;
        Tick();
    }

    public void CommitSeekPreview() { if (!disposed) seekHoldUntil = Environment.TickCount64 + 1200; }
    public void CancelSeekPreview()
    {
        if (disposed) return;
        seekPreview = null;
        seekHoldUntil = 0;
        Tick();
    }
    public void DismissUpNext()
    {
        if (disposed) return;
        upNextDismissed = true;
        ShowUpNext = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.SnapshotChanged -= OnSnapshotChanged;
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        snapshot = session.Snapshot;
        ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        var now = Environment.TickCount64;
        var publicationDelay = snapshot.CapturedAtUtc == default ? 0 : Math.Clamp((DateTimeOffset.UtcNow - snapshot.CapturedAtUtc).TotalMilliseconds, 0, 2000);
        sampleTime = now - (long)publicationDelay;
        if (IsOpening) { if (openingSince == 0) openingSince = now; }
        else openingSince = 0;
        var changedEntry = entryKey != snapshot.Entry?.ItemId;
        if (changedEntry)
        {
            entryKey = snapshot.Entry?.ItemId;
            seekPreview = null;
            seekHoldUntil = 0;
            upNextDismissed = false;
        }
        if (seekHoldUntil > 0 && !snapshot.IsSeeking && seekPreview is { } position && Math.Abs(snapshot.PositionTicks - position) < TimeSpan.TicksPerSecond)
        {
            seekPreview = null;
            seekHoldUntil = 0;
        }
        if (!Episodes.Select(e => e.ItemId).SequenceEqual(snapshot.Entries.Select(e => e.ItemId)))
        {
            Episodes.Clear();
            for (var i = 0; i < snapshot.Entries.Length; i++) Episodes.Add(new(snapshot.Entries[i], i));
        }
        foreach (var episode in Episodes)
        {
            episode.IsCurrent = episode.ItemId == snapshot.Entry?.ItemId;
            episode.CanSelect = CanControl;
        }
        foreach (var property in ProjectionProperties) OnPropertyChanged(property);
        Tick();
    }

    private static readonly string[] ProjectionProperties =
    [nameof(Snapshot), nameof(CanControl), nameof(IsOpening), nameof(IsFailed), nameof(IsExternal), nameof(ShowExternalPanel), nameof(IsSlowOpening), nameof(IsBuffering),
        nameof(CanPrevious), nameof(CanNext), nameof(HasEpisodes), nameof(IsPaused), nameof(PauseGlyph), nameof(PauseAccessibleName), nameof(MuteAccessibleName), nameof(VolumeGlyph), nameof(RateText),
        nameof(DurationText), nameof(DurationSeconds), nameof(Volume), nameof(ErrorText), nameof(Title), nameof(Subtitle), nameof(ShellTitle), nameof(NextTitle)];

    public static string FormatTicks(long ticks)
    {
        var time = TimeSpan.FromTicks(Math.Max(0, ticks));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}

public sealed partial class PlayerEpisodeViewModel : ObservableObject
{
    public PlayerEpisodeViewModel(PlaybackEntry entry, int index)
    {
        ItemId = entry.ItemId;
        Title = entry.EpisodeName ?? entry.Title;
        Number = GetEpisodeNumber(entry, index).ToString(CultureInfo.InvariantCulture);
    }
    public string ItemId { get; }
    public string Title { get; }
    public string Number { get; }
    public string AccessibleName => $"第 {Number} 集，{Title}" + (IsCurrent ? "，正在播放" : "");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsCurrent { get; set; }
    [ObservableProperty]
    public partial bool CanSelect { get; set; }

    private static int GetEpisodeNumber(PlaybackEntry entry, int index)
    {
        if (entry.EpisodeNumber is { } number) return number;
        var label = entry.EpisodeLabel ?? "";
        var end = label.Length;
        while (end > 0 && !char.IsAsciiDigit(label[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(label[start - 1])) start--;
        if (start < end && int.TryParse(label.AsSpan(start, end - start), CultureInfo.InvariantCulture, out number)) return number;
        var title = entry.Title;
        for (var i = 0; i + 1 < title.Length; i++)
        {
            if ((title[i] is 'E' or 'e') && (title[..i].Contains('S') || title[..i].Contains('s')))
            {
                end = i + 1;
                while (end < title.Length && char.IsAsciiDigit(title[end])) end++;
                if (int.TryParse(title.AsSpan(i + 1, end - i - 1), CultureInfo.InvariantCulture, out number)) return number;
            }
        }
        return index + 1;
    }
}
