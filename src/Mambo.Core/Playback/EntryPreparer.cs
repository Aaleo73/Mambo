using System.Collections.Immutable;
using System.Globalization;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Persistence;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

public sealed class EntryPreparer : IEntryPreparer
{
    private readonly EmbyApi api;
    private readonly StreamUrlResolver resolver;
    private readonly Guid deviceId;
    private readonly string subtitleDirectory;
    private readonly RequestScheduler? scheduler;
    private readonly PlaybackTargetResolver targets;
    private readonly SeasonPlan plans;

    public EntryPreparer(EmbyApi api, StreamUrlResolver resolver, Guid deviceId, AppPaths paths, RequestScheduler? scheduler = null)
    {
        this.api = api; this.resolver = resolver; this.deviceId = deviceId; this.scheduler = scheduler;
        subtitleDirectory = Path.Combine(paths.Root, "cache", "playback-subtitles");
        targets = new(api, scheduler); plans = new(targets);
    }

    public async Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken)
    {
        var target = await targets.ResolveAsync(account, request, cancellationToken).ConfigureAwait(false);
        return await plans.BuildAsync(account, target.Item, target.StartTicks, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken)
    {
        if (EmbyMapper.Identity(entry.ItemId) is null || startTicks < 0) throw PlaybackTargetResolver.Invalid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
        // POST PlaybackInfo 每次登记服务器会话，禁止以缓存或本地 PlaySessionId 替代。
        var info = scheduler is null ? await api.PlaybackInfoAsync(account, entry.ItemId, startTicks, linked.Token).ConfigureAwait(false) :
            await scheduler.RunAsync(ct => api.PlaybackInfoAsync(account, entry.ItemId, startTicks, ct), idempotent: false, scopeToken: linked.Token).ConfigureAwait(false);
        var candidates = StreamCandidateBuilder.Build(account, entry.ItemId, deviceId, info);
        if (candidates.IsEmpty) throw PlaybackTargetResolver.NotPlayable();
        return new(entry, info, candidates, BuildSubtitles(account, entry.ItemId, info.MediaSources), startTicks);
    }

    public async Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken)
    {
        if (candidateIndex < 0 || candidateIndex >= entry.Candidates.Length) throw PlaybackTargetResolver.Invalid();
        var candidate = entry.Candidates[candidateIndex];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
        try
        {
            var url = await resolver.ResolveAsync(candidate.Address, account.Address.Uri, account.Secret.AccessToken,
                candidate.RequiredHeaders, linked.Token).ConfigureAwait(false);
            var options = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
            options.Add(new("http-header-fields", HeaderFields(url.Headers)));
            options.Add(new("force-media-title", entry.Entry.Title));
            if (entry.StartTicks > 0) options.Add(new("start", (entry.StartTicks / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture)));
            return new(candidate, url, options.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (AppException) { throw; }
        catch (TimeoutException) { throw new AppException(ErrorText.Network("解析片源")); }
        catch (InvalidOperationException) { throw new AppException(new(AppErrorKind.Player, ErrorCodes.PlaybackFailed, "片源暂时无法打开。", true, "解析片源")); }
        catch (ArgumentException) { throw new AppException(ErrorText.InvalidResponse("解析片源")); }
    }

    public async Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken)
    {
        var loaded = ImmutableArray.CreateBuilder<ResolvedSubtitle>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
        try
        {
            foreach (var subtitle in entry.Subtitles.Where(value => ReferenceEquals(value.MediaSource, selectedCandidate.Candidate.MediaSource)).Take(32))
            {
                linked.Token.ThrowIfCancellationRequested();
                string? path = null;
                try
                {
                    var bytes = await resolver.DownloadSubtitleAsync(subtitle.Address, account.Address.Uri, account.Secret.AccessToken,
                        StreamUrlResolver.SanitizeHeaders(subtitle.MediaSource.RequiredHttpHeaders), linked.Token).ConfigureAwait(false);
                    Directory.CreateDirectory(subtitleDirectory);
                    path = Path.Combine(subtitleDirectory, Guid.NewGuid().ToString("N") + "." + (StreamCandidateBuilder.SafeFormat(
                        subtitle.MediaSource.MediaStreams?.FirstOrDefault(stream => stream is not null && stream.Index == subtitle.StreamIndex)?.Codec) ?? "srt"));
                    await File.WriteAllBytesAsync(path, bytes, linked.Token).ConfigureAwait(false);
                    loaded.Add(new(subtitle, path));
                }
                catch (OperationCanceledException) { if (path is not null) Delete(path); throw; }
                // 一条外挂字幕失败不会阻止主视频播放；异常原文不跨边界。
                catch (Exception error) when (error is AppException or InvalidOperationException or TimeoutException or ArgumentException or IOException or UnauthorizedAccessException)
                { if (path is not null) Delete(path); }
            }
            return loaded.ToImmutable();
        }
        catch
        {
            foreach (var subtitle in loaded) Delete(subtitle.LocalPath);
            throw;
        }
    }

    public Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles)
    {
        foreach (var subtitle in subtitles)
            if (Path.GetDirectoryName(Path.GetFullPath(subtitle.LocalPath)) == subtitleDirectory) Delete(subtitle.LocalPath);
        return Task.CompletedTask;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ImmutableArray<ExternalSubtitle> BuildSubtitles(AccountSession account, string itemId, EmbyMediaSource[]? sources)
    {
        var subtitles = ImmutableArray.CreateBuilder<ExternalSubtitle>();
        foreach (var source in sources ?? [])
        {
            if (source is null) continue;
            foreach (var stream in source.MediaStreams ?? [])
            {
                if (stream is null || stream.Type?.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) != true ||
                    stream.IsExternal != true || stream.Index is not >= 0) continue;
                var url = StreamCandidateBuilder.ResolveAddress(account, stream.DeliveryUrl);
                if (url is null && EmbyMapper.Identity(source.Id) is { } sourceId && StreamCandidateBuilder.SafeFormat(stream.Codec) is { } format)
                    url = account.Address.Endpoint("Videos/" + EmbyApi.Escape(itemId) + "/" + EmbyApi.Escape(sourceId) + "/Subtitles/" + stream.Index + "/Stream." + format);
                if (url is null) continue;
                var title = stream.DisplayTitle ?? stream.Title ?? "字幕 " + (stream.Index + 1);
                if (title.Length > 96) title = title[..96];
                var language = stream.Language ?? "";
                if (language.Length > 32) language = language[..32];
                subtitles.Add(new(url, stream.Index.Value, title, language, source));
            }
        }
        return subtitles.ToImmutable();
    }

    public static string HeaderFields(IReadOnlyDictionary<string, string> headers) => string.Join(",", headers.Select(header =>
        (header.Key + ": " + header.Value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)));
}
