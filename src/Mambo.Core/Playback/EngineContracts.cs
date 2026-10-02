using System.Threading.Channels;
using Mambo.Core.Contracts;

namespace Mambo.Core.Playback;

// 后端引擎边界；前端只依赖 Contracts 中的播放会话，不直接消费 native 事件。
public interface IPlayerEngine : IAsyncDisposable
{
    EngineKind Kind { get; }
    ChannelReader<EngineEvent> Events { get; }
    ValueTask<long> LoadAsync(string url, LoadMode mode,
        IReadOnlyList<KeyValuePair<string, string>> fileOptions, CancellationToken cancellationToken);
    ValueTask CommandAsync(ReadOnlyMemory<string> arguments, CancellationToken cancellationToken);
    ValueTask SetAsync(string propertyName, MpvValue value, CancellationToken cancellationToken);
}

public enum LoadMode { Replace, Append }
public enum EngineEndReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

public enum EngineProperty
{
    TimePosition, Duration, Pause, PausedForCache, Seeking, CoreIdle, IdleActive, EofReached,
    Speed, Volume, Mute, AudioTrack, SubtitleTrack, TrackList, VideoParameters,
    VideoTargetParameters, HardwareDecoder, DemuxerCacheState, PlaylistPosition, PlaylistCount, AudioOutput,
}

// 无 object/dynamic 或反射：数据在事件线程上复制后才能离开 native 调用。
public abstract record MpvValue
{
    public sealed record Text(string Value) : MpvValue;
    public sealed record WholeNumber(long Value) : MpvValue;
    public sealed record Number(double Value) : MpvValue;
    public sealed record Flag(bool Value) : MpvValue;
    public sealed record Array(IReadOnlyList<MpvValue?> Values) : MpvValue;
    public sealed record Map(IReadOnlyDictionary<string, MpvValue?> Values) : MpvValue;
}

public abstract record EngineEvent
{
    public sealed record StartFile(long EntryId) : EngineEvent;
    public sealed record FileLoaded : EngineEvent;
    public sealed record PlaybackRestart : EngineEvent;
    public sealed record EndFile(long EntryId, EngineEndReason Reason, int Error) : EngineEvent;
    public sealed record Shutdown : EngineEvent;
    public sealed record PropertyChanged(EngineProperty Property, MpvValue? Value) : EngineEvent;
    public sealed record VideoReconfig : EngineEvent;
    public sealed record QueueOverflow : EngineEvent;
    public sealed record Failure(string Text) : EngineEvent;
}
