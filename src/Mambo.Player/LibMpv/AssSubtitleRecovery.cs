using System.Globalization;

namespace Mambo.Player.LibMpv;

/// <summary>兼容字幕仅存在于当前原生条目内存中；内部替代轨道不进入 Core 的轨道、选轨偏好或播放上报。</summary>
internal sealed class AssSubtitleRecovery(MpvCore core) : IAsyncDisposable
{
    private const string TitlePrefix = "Mambo.AssRecovery:";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<long, long> replacements = [];
    private readonly HashSet<long> inspected = [];
    private long entryId = -1;

    internal async Task SetSubtitleAsync(MpvValue value, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ResetEntry();
            var native = Id(value) is { } original && replacements.TryGetValue(original, out var replacement)
                ? new MpvValue.WholeNumber(replacement) : value;
            await core.SetPropertyAsync("sid", native, linked.Token).ConfigureAwait(false);
            // 原生选择成功后，内部兼容事务只随引擎退出取消，避免用户取消留下未映射的半条轨道。
            await RecoverSelectedAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<MpvMessage> TransformAsync(MpvMessage message)
    {
        if (message is not (MpvMessage.StartFile or MpvMessage.FileLoaded or MpvMessage.QueueOverflow or
            MpvMessage.PropertyChanged { Name: "sid" or "track-list" or "sub-ass-extradata" })) return message;
        // 关闭时仍须转发队列尾部和 SHUTDOWN；取消内部恢复不能提前结束整个事件流。
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetEntry();
            if (!lifetime.IsCancellationRequested && message is not MpvMessage.StartFile)
            {
                try { await RecoverSelectedAsync(lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
            }
            return message switch
            {
                MpvMessage.PropertyChanged { Name: "sid" } changed => new MpvMessage.PropertyChanged("sid",
                    ProjectSid(CurrentOrObserved("sid", changed.Value))),
                MpvMessage.PropertyChanged { Name: "track-list" } changed => new MpvMessage.PropertyChanged("track-list",
                    ProjectTracks(CurrentOrObserved("track-list", changed.Value))),
                _ => message,
            };
        }
        finally { gate.Release(); }
    }

    private MpvValue? CurrentOrObserved(string property, MpvValue? observed)
    {
        if (lifetime.IsCancellationRequested) return observed;
        try { return core.GetProperty(property); }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { return observed; }
    }

    private void ResetEntry()
    {
        var current = core.CurrentEntryId;
        // EOF 前排队的最后一批属性仍需要旧映射；直到新条目实际开始才丢弃。
        if (current < 0 || entryId == current) return;
        entryId = current;
        replacements.Clear();
        inspected.Clear();
    }

    private async Task RecoverSelectedAsync(CancellationToken token)
    {
        if (entryId < 0 || core.CurrentEntryId != entryId || Id(core.GetProperty("sid")) is not { } selected) return;
        if (replacements.TryGetValue(selected, out var knownReplacement))
        {
            // 原生默认选轨也可能重新选回原轨道；复用兼容轨道，不再次解析或加载。
            try { await core.SetPropertyAsync("sid", new MpvValue.WholeNumber(knownReplacement), token).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            return;
        }
        if (inspected.Contains(selected) || replacements.ContainsValue(selected)) return;
        var tracks = Tracks(core.GetProperty("track-list"));
        var original = tracks.FirstOrDefault(track => Id(track.Values.GetValueOrDefault("id")) == selected);
        if (original?.Values.GetValueOrDefault("codec") is not MpvValue.Text { Value: "ass" } ||
            original.Values.GetValueOrDefault("external") is MpvValue.Flag { Value: true }) return;
        if (core.GetProperty("sub-ass-extradata") is not MpvValue.Text extra) return;
        inspected.Add(selected);
        if (!AssScriptCompatibility.TryRepair(extra.Value, out var script)) return;

        var expectedEntry = entryId;
        var marker = TitlePrefix + Guid.NewGuid().ToString("N");
        try
        {
            if (core.CurrentEntryId != expectedEntry) return;
            await core.CommandAsync(new[] { "sub-add", "memory://" + script, "auto", marker }.AsMemory(), token).ConfigureAwait(false);
            var added = Tracks(core.GetProperty("track-list")).FirstOrDefault(track =>
                track.Values.GetValueOrDefault("title") is MpvValue.Text title && title.Value == marker);
            if (Id(added?.Values.GetValueOrDefault("id")) is not { } replacement) return;
            if (core.CurrentEntryId != expectedEntry)
            {
                // 加载期间已经切集，只移除本次创建的轨道，不向新条目写入旧选择。
                await core.CommandAsync(new[] { "sub-remove", replacement.ToString(CultureInfo.InvariantCulture) }.AsMemory(), token).ConfigureAwait(false);
                return;
            }
            replacements[selected] = replacement;
            if (Id(core.GetProperty("sid")) == selected)
                await core.SetPropertyAsync("sid", new MpvValue.WholeNumber(replacement), token).ConfigureAwait(false);
        }
        // 未知损坏继续交给原生播放器；字幕兼容失败不得中断视频。
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
    }

    private MpvValue? ProjectSid(MpvValue? value)
    {
        if (Id(value) is not { } selected) return value;
        foreach (var (original, replacement) in replacements)
            if (replacement == selected) return new MpvValue.WholeNumber(original);
        return value;
    }

    private MpvValue? ProjectTracks(MpvValue? value)
    {
        if (value is not MpvValue.Array array) return value;
        var nativeSelected = array.Values.OfType<MpvValue.Map>().FirstOrDefault(track =>
            track.Values.GetValueOrDefault("type") is MpvValue.Text { Value: "sub" } &&
            track.Values.GetValueOrDefault("selected") is MpvValue.Flag { Value: true });
        var selected = Id(ProjectSid(nativeSelected?.Values.GetValueOrDefault("id")));
        return new MpvValue.Array(array.Values.Where(track => track is not MpvValue.Map map || !IsRecoveryTrack(map))
            .Select(track => track is MpvValue.Map map && map.Values.GetValueOrDefault("type") is MpvValue.Text { Value: "sub" }
                ? new MpvValue.Map(new Dictionary<string, MpvValue?>(map.Values, StringComparer.Ordinal)
                { ["selected"] = new MpvValue.Flag(Id(map.Values.GetValueOrDefault("id")) == selected) }) : track).ToArray());
    }

    private static bool IsRecoveryTrack(MpvValue.Map track) =>
        track.Values.GetValueOrDefault("title") is MpvValue.Text title && title.Value.StartsWith(TitlePrefix, StringComparison.Ordinal) &&
        track.Values.GetValueOrDefault("external-filename") is MpvValue.Text path && path.Value.StartsWith("memory://", StringComparison.Ordinal);

    private static MpvValue.Map[] Tracks(MpvValue? value) => value is MpvValue.Array array
        ? array.Values.OfType<MpvValue.Map>().Where(track => track.Values.GetValueOrDefault("type") is MpvValue.Text { Value: "sub" }).ToArray() : [];

    private static long? Id(MpvValue? value) => value switch
    {
        MpvValue.WholeNumber number when number.Value > 0 => number.Value,
        MpvValue.Text text when long.TryParse(text.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 => id,
        _ => null,
    };

    internal void Cancel() => lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Dispose();
        lifetime.Dispose();
    }
}
