namespace Mambo.Core.Contracts;

public enum SubtitleStyleKind { None, Text, Ass, Bitmap, Unknown }
public enum TrackSource { Embedded, External, Local }

/// <summary>基于 720 高度的文本字幕样式，自动保存为本机全局偏好。</summary>
public sealed record SubtitleStyleSettings
{
    public string FontFamily { get; init; } = "MiSans";
    public double FontSize { get; init; } = 38;
    public string TextColor { get; init; } = "#FFFFFF";
    public double OutlineSize { get; init; } = 1.65;
    public double BottomMargin { get; init; } = 34;
    public bool OverrideAssStyle { get; init; }
}

/// <summary>路径只用于本次导入，不可写入日志、设置或快照。</summary>
public sealed class LocalSubtitleFile(string path)
{
    public string Path { get; } = path;
    public override string ToString() => "LocalSubtitleFile { <redacted> }";
}

/// <summary>拖放开始时捕获的不透明上下文；仅创建它的播放会话可以消费。</summary>
public sealed class SubtitleImportContext
{
    internal SubtitleImportContext(object owner, PlaybackEntry entry, long generation, long revision)
    { Owner = owner; Entry = entry; Generation = generation; Revision = revision; }
    internal object Owner { get; }
    internal PlaybackEntry Entry { get; }
    internal long Generation { get; }
    internal long Revision { get; }
    public override string ToString() => "SubtitleImportContext { <opaque> }";
}
