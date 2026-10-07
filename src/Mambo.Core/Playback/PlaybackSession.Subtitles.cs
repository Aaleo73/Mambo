using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Subtitles;

namespace Mambo.Core.Playback;

public sealed partial class PlaybackSession
{
    private readonly object subtitleNativeGate = new();
    private CancellationTokenSource? subtitleNativeCancellation;
    private bool subtitleLoadsCancelled;

    private void CancelSubtitleLoads()
    {
        lock (subtitleNativeGate)
        {
            subtitleLoadsCancelled = true;
            subtitleNativeCancellation?.Cancel();
        }
    }
    public Task SetSubtitleDelayAsync(double seconds, long expectedEntryGeneration, string? expectedSubtitleTrackId,
        CancellationToken cancellationToken = default) => Command(async () =>
    {
        RequireSubtitleEntry(expectedEntryGeneration);
        if (!double.IsFinite(seconds) || seconds is < -60 or > 60 || Math.Abs(seconds * 10 - Math.Round(seconds * 10)) > 0.0001)
            throw InvalidCommand();
        if (expectedSubtitleTrackId is null || snapshot.SelectedSubtitleTrackId != expectedSubtitleTrackId)
            throw new AppException(new(AppErrorKind.Player, "playback.subtitle_stale", "字幕已改变，请重新调整。", false));
        await SetAsync("sub-delay", new MpvValue.Number(seconds)).ConfigureAwait(false);
        RequireSubtitleEntry(expectedEntryGeneration);
        desiredSubtitleDelay = seconds;
        Update(snapshot with { SubtitleDelaySeconds = seconds });
    }, cancellationToken);

    public Task SetSubtitleStyleAsync(SubtitleStyleSettings style, long expectedEntryGeneration,
        CancellationToken cancellationToken = default)
    {
        SubtitleStyle.Validate(style);
        var revision = Interlocked.Increment(ref styleRevision);
        return Command(async () =>
        {
            RequireSubtitleEntry(expectedEntryGeneration);
            if (revision != Volatile.Read(ref styleRevision)) return;
            var previous = snapshot.SubtitleStyle;
            if (snapshot.SubtitleStyleKind is SubtitleStyleKind.Bitmap or SubtitleStyleKind.Unknown ||
                snapshot.SubtitleStyleKind == SubtitleStyleKind.Ass && !style.OverrideAssStyle &&
                style != new SubtitleStyleSettings() &&
                (previous with { OverrideAssStyle = style.OverrideAssStyle }) != style)
                throw new AppException(new(AppErrorKind.Player, "playback.subtitle_style_unavailable", "当前字幕不支持此样式调整。", false));
            try { await ApplyStylePropertiesAsync(EngineRequired, style).ConfigureAwait(false); }
            catch (Exception)
            {
                try { await ApplyStylePropertiesAsync(EngineRequired, previous).ConfigureAwait(false); }
                catch (Exception) { LogSubtitleFailure("playback.subtitle_style_restore_failed"); }
                throw new AppException(new(AppErrorKind.Player, "playback.subtitle_style_failed", "字幕样式未能应用。", true));
            }
            Update(snapshot with { SubtitleStyle = style });
            if (Settings is { } settings)
            {
                try { await settings.UpdateAsync(current => current with { SubtitleStyle = style }, account.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception) { throw new AppException(new(AppErrorKind.Player, "settings.subtitle_style_save_failed", "设置保存失败。", true)); }
            }
        }, cancellationToken);
    }

    private async Task ApplyStylePropertiesAsync(IPlayerEngine owner, SubtitleStyleSettings style)
    {
        foreach (var pair in SubtitleStyle.Properties(style))
            await owner.SetAsync(pair.Key, pair.Value, lifetime.Token).ConfigureAwait(false);
    }

    public SubtitleImportContext? BeginSubtitleImport()
    {
        var current = Snapshot;
        if (!current.CanImportSubtitles || current.Entry is null) return null;
        var revision = Interlocked.Increment(ref subtitleActionRevision);
        var context = new SubtitleImportContext(this, current.Entry, current.EntryGeneration, revision);
        Post(() =>
        {
            itemSubtitleRevisions[context.Entry.ItemId] = Math.Max(itemSubtitleRevisions.GetValueOrDefault(context.Entry.ItemId), revision);
            return Task.CompletedTask;
        });
        return context;
    }

    public async Task ImportSubtitlesAsync(SubtitleImportContext context, IReadOnlyList<LocalSubtitleFile> files,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(context.Owner, this) || files.Count == 0 || LocalSubtitles is null) return;
        try
        {
            var paths = files.Select(file => file.Path).ToImmutableArray();
            Task work = Task.CompletedTask;
            await Command(() =>
            {
                // 路径读取完成时可能已切到下一集；仍使用 Drop 起点，而不是当前项目。
                work = Task.Run(() => ImportSubtitleWorkerAsync(context, paths, cancellationToken), CancellationToken.None);
                TrackPreparation(work);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { LogSubtitleFailure("playback.subtitle_import_failed"); }
    }

    private async Task ImportSubtitleWorkerAsync(SubtitleImportContext context, ImmutableArray<string> paths, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token, token);
        try
        {
            var result = await LocalSubtitles!.ImportAsync(account, context.Entry, paths, linked.Token).ConfigureAwait(false);
            // 不等待 actor：关闭会等待后台文件事务，反向等待 actor 会造成关闭死锁。
            Post(async () =>
            {
                if (closing || account.Token.IsCancellationRequested) return;
                foreach (var group in result.Items.GroupBy(item => item.ItemId))
                {
                    try
                    {
                        var first = group.First().File;
                        var newer = itemSubtitleRevisions.GetValueOrDefault(group.Key) > context.Revision;
                        if (group.Key == context.Entry.ItemId && active?.Generation == context.Generation &&
                            active.Prepared.Entry.ItemId == group.Key && Volatile.Read(ref subtitleActionRevision) > context.Revision) newer = true;
                        if (!newer)
                        {
                            itemSubtitleRevisions[group.Key] = context.Revision;
                            await LocalSubtitles.SetAdoptedAsync(account, group.Key, first.Id, account.Token).ConfigureAwait(false);
                            if (TrackPreferences is { } preferences)
                                await preferences.SaveAsync(account, new(group.Key, ""), TrackKind.Subtitle,
                                    new TrackChoice(false, LocalSubtitleId: first.Id), trackEpoch, true, account.Token).ConfigureAwait(false);
                        }
                        if (active is { Loaded: true, Ended: false } entry && entry.Generation == context.Generation &&
                            entry.Prepared.Entry.ItemId == group.Key && !switching)
                        {
                            foreach (var imported in group) entry.LocalFiles[imported.File.Id] = imported.File;
                            if (!newer && context.Revision == Volatile.Read(ref subtitleActionRevision))
                            { entry.AdoptedSubtitleId = first.Id; entry.SubtitleManual = false; }
                            foreach (var imported in group)
                                await LoadLocalFileAsync(entry, imported.File).ConfigureAwait(false);
                            await ApplyTrackSelectionAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception) { LogSubtitleFailure("playback.subtitle_import_failed"); }
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { LogSubtitleFailure("playback.subtitle_import_failed"); }
    }

    private async Task LoadLocalSubtitlesAsync(LoadedEntry entry)
    {
        if (LocalSubtitles is not { } library) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        var expectedGeneration = entry.Generation;
        try
        {
            var stored = await library.GetForItemAsync(account, entry.Prepared.Entry.ItemId, linked.Token).ConfigureAwait(false);
            Post(async () =>
            {
                if (closing || active != entry || entry.Ended || entry.Generation != expectedGeneration) return;
                // 已完成的新导入拥有更新的采用项，读取旧索引的结果不得覆盖它。
                entry.AdoptedSubtitleId ??= stored.AdoptedSubtitleId;
                foreach (var file in stored.Files)
                {
                    entry.LocalFiles.TryAdd(file.Id, file);
                    await LoadLocalFileAsync(entry, file).ConfigureAwait(false);
                }
                await ApplyTrackSelectionAsync().ConfigureAwait(false);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { LogSubtitleFailure("playback.subtitle_library_read_failed"); }
    }

    private async Task LoadLocalFileAsync(LoadedEntry entry, LocalSubtitleFileInfo file)
    {
        try { await AddSubtitleAsync(entry, file.ManagedPath, file.DisplayName, "").ConfigureAwait(false); }
        catch (Exception) { LogSubtitleFailure("playback.local_subtitle_load_failed"); }
    }

    private async Task AddSubtitleAsync(LoadedEntry entry, string path, string title, string language)
    {
        if (engine is null || closing || active != entry || entry.Ended || !IsNativeEntryCurrent(entry) || !entry.AddedSubtitlePaths.Add(path)) return;
        subtitlePathGenerations[path] = entry.Generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account.Token);
        lock (subtitleNativeGate)
        {
            if (subtitleLoadsCancelled) return;
            subtitleNativeCancellation = cancellation;
        }
        try
        {
            await engine.CommandAsync(new[] { "sub-add", path, "auto", title, language }, cancellation.Token).ConfigureAwait(false);
            // 官方 auto 允许默认选轨副作用；成功回包后和属性事件处都维护明确手选。
            if (entry.SubtitleManual && IsNativeEntryCurrent(entry))
                await SetAsync("sid", new MpvValue.Text(entry.ManualSubtitleId ?? "no")).ConfigureAwait(false);
        }
        catch { entry.AddedSubtitlePaths.Remove(path); throw; }
        finally { lock (subtitleNativeGate) if (ReferenceEquals(subtitleNativeCancellation, cancellation)) subtitleNativeCancellation = null; }
    }
}
