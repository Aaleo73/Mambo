using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Subtitles;

namespace Mambo.Core.Playback;

public sealed partial class PlaybackSession
{
    internal TrackPreferences? TrackPreferences { get; set; }
    internal LocalSubtitleLibrary? LocalSubtitles { get; set; }
    private TrackPreferenceEpoch trackEpoch = new("auto", "zh", 0, 0);
    private long entryGeneration;
    private long subtitleActionRevision;
    private long styleRevision;
    private double desiredSubtitleDelay;
    private readonly Dictionary<string, long> itemSubtitleRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> subtitlePathGenerations = new(StringComparer.OrdinalIgnoreCase);
    private NativeEntryStamp? nativeEntryStamp;
    private sealed record NativeEntryStamp(IPlayerEngine Engine, long Id);
    private bool IsNativeEntryCurrent(LoadedEntry entry) => Volatile.Read(ref nativeEntryStamp) is { } stamp &&
        ReferenceEquals(stamp.Engine, engine) && stamp.Id == entry.NativeId;

    private void CaptureTrackSettings()
    {
        var settings = Settings?.Current ?? new AppSettings();
        trackEpoch = TrackPreferences?.Capture() ?? new(settings.PreferredAudioLanguage, settings.PreferredSubtitleLanguage, 0, 0);
        Update(snapshot with { SubtitleStyle = SubtitleStyle.Normalize(settings.SubtitleStyle) });
    }

    private async Task InitializeSubtitleEngineAsync(IPlayerEngine owner)
    {
        if (owner.Kind != EngineKind.Embedded) return;
        await owner.SetAsync("alang", new MpvValue.Text(TrackSelection.LanguageCodes(trackEpoch.AudioLanguage, false)), lifetime.Token).ConfigureAwait(false);
        await owner.SetAsync("slang", new MpvValue.Text(TrackSelection.LanguageCodes(trackEpoch.SubtitleLanguage, true)), lifetime.Token).ConfigureAwait(false);
        try { await ApplyStylePropertiesAsync(owner, snapshot.SubtitleStyle).ConfigureAwait(false); }
        catch (Exception)
        {
            LogSubtitleFailure("playback.subtitle_style_restore_failed");
            var defaults = new SubtitleStyleSettings();
            try { await ApplyStylePropertiesAsync(owner, defaults).ConfigureAwait(false); Update(snapshot with { SubtitleStyle = defaults }); }
            catch (Exception) { LogSubtitleFailure("playback.subtitle_style_restore_failed"); }
        }
    }

    private void ResetTrackEntry(LoadedEntry entry)
    {
        entry.Generation = Interlocked.Increment(ref entryGeneration);
        entry.AudioManual = entry.SubtitleManual = false;
        entry.ManualAudioId = entry.ManualSubtitleId = null;
        entry.Audio = entry.SubtitleTracks = [];
        entry.LocalFiles.Clear();
        entry.LocalTrackIds.Clear();
        entry.AddedSubtitlePaths.Clear();
        entry.ForeignSubtitleIds.Clear();
        entry.AdoptedSubtitleId = null;
        desiredSubtitleDelay = 0;
    }

    private async Task HandleTrackPropertyAsync(EngineEvent.PropertyChanged property)
    {
        if (property.Property == EngineProperty.SubtitleDelay)
        {
            if (Numeric(property.Value) is { } value && Math.Abs(value - desiredSubtitleDelay) < 0.0001)
                Update(snapshot with { SubtitleDelaySeconds = value });
            return;
        }
        if (property.Property == EngineProperty.TrackList && property.Value is MpvValue.Array tracks)
        {
            var foreignTracks = Tracks(tracks);
            foreach (var id in active is { } current && IsNativeEntryCurrent(current) ? foreignTracks : [])
                try { await EngineRequired.CommandAsync(new[] { "sub-remove", id }, lifetime.Token).ConfigureAwait(false); }
                catch (Exception) { LogSubtitleFailure("playback.stale_subtitle_remove_failed"); }
            await ApplyTrackSelectionAsync().ConfigureAwait(false);
            return;
        }
        if (engine?.Kind == EngineKind.Embedded && active is { Loaded: true, Ended: false } entry && IsNativeEntryCurrent(entry) &&
            property.Property is EngineProperty.SubtitleTrack or EngineProperty.AudioTrack)
        {
            var audio = property.Property == EngineProperty.AudioTrack;
            var id = TrackId(property.Value);
            if (!audio && id is not null && entry.ForeignSubtitleIds.Contains(id))
            {
                try { await SetAsync("sid", new MpvValue.Text(snapshot.SelectedSubtitleTrackId ?? "no")).ConfigureAwait(false); }
                catch (Exception) { LogSubtitleFailure("playback.track_restore_failed"); }
                return;
            }
            if ((audio ? entry.AudioManual : entry.SubtitleManual) &&
                id != (audio ? entry.ManualAudioId : entry.ManualSubtitleId))
            {
                // sub-add auto 仍可能触发原生默认选轨；以当集手选为准，不将旧属性投影到界面。
                var desired = audio ? entry.ManualAudioId : entry.ManualSubtitleId;
                try { await SetAsync(audio ? "aid" : "sid", new MpvValue.Text(desired ?? "no")).ConfigureAwait(false); }
                catch (Exception) { LogSubtitleFailure("playback.track_restore_failed"); }
                return;
            }
            if (!audio && snapshot.SelectedSubtitleTrackId != id)
                try { await ResetSubtitleDelayAsync().ConfigureAwait(false); }
                catch (Exception) { LogSubtitleFailure("playback.subtitle_delay_reset_failed"); }
            PropertyChanged(property);
            PublishTracks(entry);
            await ApplyTrackSelectionAsync().ConfigureAwait(false);
            return;
        }
        PropertyChanged(property);
    }

    private List<string> Tracks(MpvValue.Array values)
    {
        if (active is not { } entry) return [];
        var foreignTracks = new List<string>();
        var audio = ImmutableArray.CreateBuilder<TrackInfo>();
        var subtitles = ImmutableArray.CreateBuilder<TrackInfo>();
        entry.TrackIndexes.Clear();
        entry.LocalTrackIds.Clear();
        foreach (var track in values.Values.OfType<MpvValue.Map>())
        {
            var type = Text(track.Values.GetValueOrDefault("type"));
            if (type is not ("audio" or "sub")) continue;
            var kind = type == "audio" ? TrackKind.Audio : TrackKind.Subtitle;
            var id = TrackId(track.Values.GetValueOrDefault("id"));
            if (id is null) continue;
            var path = Text(track.Values.GetValueOrDefault("external-filename"));
            if (kind == TrackKind.Subtitle && path is not null && subtitlePathGenerations.TryGetValue(path, out var ownerGeneration) &&
                ownerGeneration != entry.Generation)
            { entry.ForeignSubtitleIds.Add(id); foreignTracks.Add(id); continue; }
            entry.ForeignSubtitleIds.Remove(id);
            var local = path is null ? null : entry.LocalFiles.Values.FirstOrDefault(file => SameSubtitlePath(file.ManagedPath, path));
            var external = local is not null || path is not null || track.Values.GetValueOrDefault("external") is MpvValue.Flag { Value: true };
            var title = local?.DisplayName ?? CleanLabel(Text(track.Values.GetValueOrDefault("title")));
            var language = CleanLabel(Text(track.Values.GetValueOrDefault("lang")));
            var codec = CleanLabel(Text(track.Values.GetValueOrDefault("codec")));
            var channels = CleanLabel(Text(track.Values.GetValueOrDefault("demux-channel-layout"))) ??
                Numeric(track.Values.GetValueOrDefault("demux-channel-count"))?.ToString(CultureInfo.InvariantCulture);
            var source = local is not null ? TrackSource.Local : external ? TrackSource.External : TrackSource.Embedded;
            var sourceLabel = source switch { TrackSource.Local => "本地", TrackSource.External => "外挂", _ => "内封" };
            var label = string.Join(" · ", new[] { title, language, codec, kind == TrackKind.Audio ? channels : null, sourceLabel }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            var projected = new TrackInfo(id, kind, label.Length > 96 ? label[..96] : label)
            {
                Title = title, Language = language, Codec = codec, AudioChannels = kind == TrackKind.Audio ? channels : null,
                Source = source, IsDefault = track.Values.GetValueOrDefault("default") is MpvValue.Flag { Value: true },
                IsForced = track.Values.GetValueOrDefault("forced") is MpvValue.Flag { Value: true },
                SubtitleStyleKind = kind == TrackKind.Subtitle ? StyleKind(codec ?? local?.Format) : SubtitleStyleKind.None,
            };
            (kind == TrackKind.Audio ? audio : subtitles).Add(projected);
            if (local is not null) entry.LocalTrackIds[id] = local.Id;
            else if (external)
            {
                var server = path is null ? null : entry.Subtitles.FirstOrDefault(item => SameSubtitlePath(item.LocalPath, path));
                if (server is not null) entry.TrackIndexes[(kind, id)] = server.Subtitle.StreamIndex;
            }
            else if (Numeric(track.Values.GetValueOrDefault("ff-index")) is { } index && index >= 0 && index <= int.MaxValue)
            {
                var mediaType = kind == TrackKind.Audio ? "Audio" : "Subtitle";
                if (entry.Candidate?.Candidate.MediaSource.MediaStreams?.Any(stream => stream.Index == (int)index && stream.Type == mediaType) == true)
                    entry.TrackIndexes[(kind, id)] = (int)index;
            }
        }
        entry.Audio = Distinguish(audio.ToImmutable());
        entry.SubtitleTracks = Distinguish(subtitles.ToImmutable());
        PublishTracks(entry);
        return foreignTracks;
    }

    private static ImmutableArray<TrackInfo> Distinguish(ImmutableArray<TrackInfo> tracks)
    {
        var duplicateLabels = tracks.GroupBy(track => track.Label).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return tracks.Select((track, index) => duplicateLabels.Contains(track.Label) ? track with
        { Label = (track.Label.Length > 85 ? track.Label[..85] : track.Label) + " · " + (index + 1).ToString(CultureInfo.InvariantCulture) } : track).ToImmutableArray();
    }

    private void PublishTracks(LoadedEntry entry) => Update(snapshot with
    {
        AudioTracks = MenuTracks(entry.Audio, snapshot.SelectedAudioTrackId),
        SubtitleTracks = MenuTracks(entry.SubtitleTracks, snapshot.SelectedSubtitleTrackId),
    });

    private static ImmutableArray<TrackInfo> MenuTracks(ImmutableArray<TrackInfo> tracks, string? selected) => tracks
        .OrderByDescending(track => track.Id == selected).ThenByDescending(track => track.Source == TrackSource.Local).Take(32).ToImmutableArray();

    private static SubtitleStyleKind StyleKind(string? codec) => codec?.ToLowerInvariant().TrimStart('.') switch
    {
        "ass" or "ssa" => SubtitleStyleKind.Ass,
        "subrip" or "srt" or "webvtt" or "vtt" or "text" or "mov_text" => SubtitleStyleKind.Text,
        "hdmv_pgs_subtitle" or "pgs" or "dvd_subtitle" or "dvb_subtitle" or "vobsub" or "xsub" => SubtitleStyleKind.Bitmap,
        _ => SubtitleStyleKind.Unknown,
    };

    private static bool SameSubtitlePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private async Task ApplyTrackSelectionAsync()
    {
        if (engine?.Kind != EngineKind.Embedded || active is not { Loaded: true, Ended: false } entry || switching || !IsNativeEntryCurrent(entry)) return;
        try
        {
            foreach (var kind in new[] { TrackKind.Audio, TrackKind.Subtitle })
            {
                if (!IsNativeEntryCurrent(entry)) return;
                var tracks = kind == TrackKind.Audio ? entry.Audio : entry.SubtitleTracks;
                string? id;
                bool selected;
                if (kind == TrackKind.Audio && entry.AudioManual) { id = entry.ManualAudioId; selected = true; }
                else if (kind == TrackKind.Subtitle && entry.SubtitleManual) { id = entry.ManualSubtitleId; selected = true; }
                else
                {
                    selected = false; id = null;
                    if (kind == TrackKind.Subtitle)
                    {
                        selected = TryChoice(TrackPreferences?.Get(account, entry.Prepared.Entry, kind, trackEpoch, exactItem: true), entry, tracks, out id);
                        if (!selected && entry.AdoptedSubtitleId is { } adopted)
                        {
                            id = entry.LocalTrackIds.FirstOrDefault(pair => pair.Value == adopted).Key;
                            selected = id is not null;
                        }
                    }
                    if (!selected) selected = TryChoice(TrackPreferences?.Get(account, entry.Prepared.Entry, kind, trackEpoch), entry, tracks, out id);
                    if (!selected)
                    {
                        var language = kind == TrackKind.Audio ? trackEpoch.AudioLanguage : trackEpoch.SubtitleLanguage;
                        if (language == "off" && kind == TrackKind.Subtitle) selected = true;
                        else if (language != "auto")
                        {
                            foreach (var code in TrackSelection.LanguageCodes(language, kind == TrackKind.Subtitle).Split(','))
                            {
                                var matches = tracks.Where(track => string.Equals(track.Language, code, StringComparison.OrdinalIgnoreCase)).ToArray();
                                id = matches.FirstOrDefault(track => track.Id == (kind == TrackKind.Audio ? snapshot.SelectedAudioTrackId : snapshot.SelectedSubtitleTrackId))?.Id
                                    ?? matches.FirstOrDefault(track => track.IsDefault)?.Id ?? matches.FirstOrDefault()?.Id;
                                if (id is not null) { selected = true; break; }
                            }
                        }
                    }
                }
                if (selected && (id is null || tracks.Any(track => track.Id == id)))
                    await SetSelectedTrackAsync(id, kind).ConfigureAwait(false);
            }
        }
        catch (Exception) { LogSubtitleFailure("playback.track_restore_failed"); }
    }

    private static bool TryChoice(TrackChoice? choice, LoadedEntry entry, ImmutableArray<TrackInfo> tracks, out string? id)
    {
        id = null;
        if (choice is null) return false;
        if (choice.Disabled) return true;
        if (choice.LocalSubtitleId is { } localId)
            id = entry.LocalTrackIds.FirstOrDefault(pair => pair.Value == localId).Key;
        else if (choice.Fingerprint is { } fingerprint) id = TrackSelection.Match(fingerprint, tracks.Where(track => track.Source != TrackSource.Local));
        return id is not null;
    }

    public Task SelectAudioTrackAsync(string? trackId, CancellationToken cancellationToken = default) =>
        SelectAudioTrackAsync(trackId, Snapshot.EntryGeneration, cancellationToken);
    public Task SelectSubtitleTrackAsync(string? trackId, CancellationToken cancellationToken = default) =>
        SelectSubtitleTrackAsync(trackId, Snapshot.EntryGeneration, cancellationToken);
    public Task SelectAudioTrackAsync(string? trackId, long expectedEntryGeneration, CancellationToken cancellationToken = default) =>
        SelectTrackAsync(trackId, TrackKind.Audio, expectedEntryGeneration, cancellationToken);
    public Task SelectSubtitleTrackAsync(string? trackId, long expectedEntryGeneration, CancellationToken cancellationToken = default) =>
        SelectTrackAsync(trackId, TrackKind.Subtitle, expectedEntryGeneration, cancellationToken);

    private Task SelectTrackAsync(string? id, TrackKind kind, long expectedGeneration, CancellationToken token)
    {
        var revision = kind == TrackKind.Subtitle ? Interlocked.Increment(ref subtitleActionRevision) : 0;
        return Command(async () =>
        {
            if (engine?.Kind == EngineKind.External)
            {
                if (snapshot.EntryGeneration != expectedGeneration ||
                    id is not null && !(kind == TrackKind.Audio ? snapshot.AudioTracks : snapshot.SubtitleTracks).Any(track => track.Id == id))
                    throw InvalidCommand();
                await SetSelectedTrackAsync(id, kind, force: true).ConfigureAwait(false);
                return;
            }
            var entry = RequireSubtitleEntry(expectedGeneration);
            var track = (kind == TrackKind.Audio ? entry.Audio : entry.SubtitleTracks).FirstOrDefault(track => track.Id == id);
            if (id is not null && track is null) throw InvalidCommand();
            await SetSelectedTrackAsync(id, kind, force: true).ConfigureAwait(false);
            if (kind == TrackKind.Audio) { entry.AudioManual = true; entry.ManualAudioId = id; }
            else
            {
                entry.SubtitleManual = true; entry.ManualSubtitleId = id;
                itemSubtitleRevisions[entry.Prepared.Entry.ItemId] = revision;
            }
            if (TrackPreferences is { } preferences && engine?.Kind == EngineKind.Embedded && (kind != TrackKind.Audio || id is not null))
            {
                var localId = id is not null ? entry.LocalTrackIds.GetValueOrDefault(id) : null;
                var choice = new TrackChoice(id is null, track is not null && localId is null ? TrackSelection.Fingerprint(track) : null, localId);
                try
                {
                    // 导入项目上的常规选择需要压住其采用项，跨剧的选择则仍按整剧保存。
                    if (kind == TrackKind.Subtitle && (entry.LocalFiles.Count > 0 || localId is not null))
                        await preferences.SaveAsync(account, entry.Prepared.Entry, kind, choice, trackEpoch, true, account.Token).ConfigureAwait(false);
                    if (localId is null)
                        await preferences.SaveAsync(account, entry.Prepared.Entry, kind, choice, trackEpoch, false, account.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { throw new AppException(new(AppErrorKind.Persistence, "settings.track_save_failed", "设置保存失败。", true)); }
            }
        }, token);
    }

    private LoadedEntry RequireSubtitleEntry(long expectedGeneration)
    {
        if (engine?.Kind != EngineKind.Embedded || active is not { Loaded: true, Ended: false } entry || switching || !IsNativeEntryCurrent(entry) ||
            expectedGeneration != entry.Generation || snapshot.Phase is PlayerPhase.Failed or PlayerPhase.Closing or PlayerPhase.Closed)
            throw new AppException(new(AppErrorKind.Player, "playback.subtitle_stale", "播放条目已改变，请重新操作。", false));
        return entry;
    }

    private async Task SetSelectedTrackAsync(string? id, TrackKind kind, bool force = false)
    {
        var expected = engine?.Kind == EngineKind.Embedded ? RequireSubtitleEntry(snapshot.EntryGeneration) : null;
        var audio = kind == TrackKind.Audio;
        var changed = id != (audio ? snapshot.SelectedAudioTrackId : snapshot.SelectedSubtitleTrackId);
        if (force || changed) await SetAsync(audio ? "aid" : "sid", new MpvValue.Text(id ?? "no")).ConfigureAwait(false);
        if (expected is not null) RequireSubtitleEntry(expected.Generation);
        if (!audio && changed) await ResetSubtitleDelayAsync().ConfigureAwait(false);
        if (expected is not null) RequireSubtitleEntry(expected.Generation);
        Update(audio ? snapshot with { SelectedAudioTrackId = id } : snapshot with { SelectedSubtitleTrackId = id });
        if (active is { } entry) PublishTracks(entry);
    }

    private async Task ResetSubtitleDelayAsync()
    {
        if (engine?.Kind != EngineKind.Embedded) return;
        await SetAsync("sub-delay", new MpvValue.Number(0)).ConfigureAwait(false);
        desiredSubtitleDelay = 0;
        Update(snapshot with { SubtitleDelaySeconds = 0 });
    }

    private void LogSubtitleFailure(string code) => log?.Invoke(new(AppErrorKind.Player, code, "字幕操作未完成。", false));

    private sealed partial class LoadedEntry
    {
        public long Generation { get; set; }
        public bool AudioManual { get; set; }
        public bool SubtitleManual { get; set; }
        public string? ManualAudioId { get; set; }
        public string? ManualSubtitleId { get; set; }
        public ImmutableArray<TrackInfo> Audio { get; set; } = [];
        public ImmutableArray<TrackInfo> SubtitleTracks { get; set; } = [];
        public Dictionary<string, LocalSubtitleFileInfo> LocalFiles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> LocalTrackIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AddedSubtitlePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ForeignSubtitleIds { get; } = new(StringComparer.Ordinal);
        public string? AdoptedSubtitleId { get; set; }
    }
}
