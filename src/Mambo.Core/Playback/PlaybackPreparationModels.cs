using System.Collections.Immutable;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Playback;

public sealed record PreparedPlan(ImmutableArray<PlaybackEntry> Entries, int SelectedIndex, long StartTicks);

/// <summary>仅供后端使用；包含短期凭据相关数据，禁止记录或交给前端。</summary>
public sealed record PreparedEntry(PlaybackEntry Entry, EmbyPlaybackInfo PlaybackInfo,
    ImmutableArray<StreamCandidate> Candidates, ImmutableArray<ExternalSubtitle> Subtitles, long StartTicks)
{
    public override string ToString() => "PreparedEntry { <redacted> }";
}

public sealed record StreamCandidate(Uri Address, string PlayMethod, EmbyMediaSource MediaSource,
    IReadOnlyDictionary<string, string> RequiredHeaders)
{
    public override string ToString() => "StreamCandidate { <redacted> }";
}

public sealed record ExternalSubtitle(Uri Address, int StreamIndex, string Title, string Language,
    EmbyMediaSource MediaSource)
{
    public override string ToString() => "ExternalSubtitle { <redacted> }";
}

public sealed record ResolvedCandidate(StreamCandidate Candidate, ResolvedPlaybackUrl Url,
    ImmutableArray<KeyValuePair<string, string>> FileOptions)
{
    public override string ToString() => "ResolvedCandidate { <redacted> }";
}

public sealed record ResolvedSubtitle(ExternalSubtitle Subtitle, string LocalPath)
{
    public override string ToString() => "ResolvedSubtitle { <redacted> }";
}

public interface IEntryPreparer
{
    Task<PreparedPlan> ResolvePlanAsync(AccountSession account, PlayRequest request, CancellationToken cancellationToken);
    Task<PreparedEntry> PrepareAsync(AccountSession account, PlaybackEntry entry, long startTicks, CancellationToken cancellationToken);
    Task<ResolvedCandidate> ResolveCandidateAsync(AccountSession account, PreparedEntry entry, int candidateIndex, CancellationToken cancellationToken);
    Task<ImmutableArray<ResolvedSubtitle>> ResolveSubtitlesAsync(AccountSession account, PreparedEntry entry, ResolvedCandidate selectedCandidate, CancellationToken cancellationToken);
    Task ReleaseSubtitlesAsync(ImmutableArray<ResolvedSubtitle> subtitles);
}
