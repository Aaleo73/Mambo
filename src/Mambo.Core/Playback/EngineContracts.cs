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

/// <summary>内置播放器的画质能力；资源、着色器与渲染确认由 Player 实现，前端不接触此接口。</summary>
public interface IVideoQualityEngine
{
    /// <summary>加载首帧前准备预设，不等待视频渲染。</summary>
    ValueTask PrepareVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken);
    /// <summary>确认实际应用；失败或取消时恢复最近已确认的完整预设，首次确认失败恢复标准。
    /// 确认之后才失效的增强由引擎清除，并通过 <see cref="EngineEvent.VideoQualityLost"/> 通知。</summary>
    ValueTask ApplyVideoQualityAsync(VideoQualityMode mode, CancellationToken cancellationToken);
}

public enum LoadMode { Replace, Append }
public enum EngineEndReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

public enum EngineProperty
{
    TimePosition, Duration, Pause, PausedForCache, Seeking, CoreIdle, IdleActive, EofReached,
    Speed, Volume, Mute, AudioTrack, SubtitleTrack, TrackList, VideoParameters,
    VideoTargetParameters, HardwareDecoder, DemuxerCacheState, PlaylistPosition, PlaylistCount, AudioOutput, SubtitleDelay,
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
    /// <summary>已确认的画质增强之后渲染失败，引擎已清除增强并回到标准。</summary>
    public sealed record VideoQualityLost : EngineEvent;
    public sealed record Failure(string Text) : EngineEvent;
}
