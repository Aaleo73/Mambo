using System.Runtime.InteropServices;

namespace Mambo.Player.LibMpv;

internal enum MpvFormat { None, String, OsdString, Flag, Int64, Double, Node, NodeArray, NodeMap, ByteArray }
internal enum MpvEventId
{
    None = 0, Shutdown = 1, LogMessage = 2, SetPropertyReply = 4, CommandReply = 5,
    StartFile = 6, EndFile = 7, FileLoaded = 8, VideoReconfig = 17,
    AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent { public MpvEventId Id; public int Error; public ulong ReplyUserData; public nint Data; }
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty { public nint Name; public MpvFormat Format; public nint Data; }
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason; public int Error; public long PlaylistEntryId;
    public long PlaylistInsertId; public int PlaylistInsertNumEntries;
}
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct MpvNode
{
    [FieldOffset(0)] public nint Pointer;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public double Double;
    [FieldOffset(0)] public int Flag;
    [FieldOffset(8)] public MpvFormat Format;
}
[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList { public int Count; public nint Values; public nint Keys; }
[StructLayout(LayoutKind.Sequential)]
internal struct MpvByteArray { public nint Data; public nuint Size; }

// 只保存已经复制的托管值，绝不保存 mpv_wait_event 的借用指针。
public abstract record MpvValue
{
    public sealed record Text(string Value) : MpvValue;
    public sealed record WholeNumber(long Value) : MpvValue;
    public sealed record Number(double Value) : MpvValue;
    public sealed record Flag(bool Value) : MpvValue;
    public sealed record Array(IReadOnlyList<MpvValue?> Values) : MpvValue;
    public sealed record Map(IReadOnlyDictionary<string, MpvValue?> Values) : MpvValue;
}

public abstract record MpvMessage
{
    public sealed record StartFile(long EntryId) : MpvMessage;
    public sealed record EndFile(long EntryId, int Reason, int Error) : MpvMessage;
    public sealed record FileLoaded : MpvMessage;
    public sealed record PlaybackRestart : MpvMessage;
    public sealed record VideoReconfig : MpvMessage;
    public sealed record PropertyChanged(string Name, MpvValue? Value) : MpvMessage;
    public sealed record SwapChainChanged(MpvSwapChain Reference) : MpvMessage;
    public sealed record QueueOverflow : MpvMessage;
    public sealed record Shutdown : MpvMessage;
    public sealed record Failure(string Text) : MpvMessage;
}
