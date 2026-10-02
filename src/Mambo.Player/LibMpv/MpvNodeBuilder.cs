using System.Runtime.InteropServices;

namespace Mambo.Player.LibMpv;

/// <summary>构造调用方拥有的 node 树；用自己的分配器释放，不能交给 mpv_free_node_contents。</summary>
internal sealed unsafe class MpvNodeBuilder : IDisposable
{
    private readonly List<nint> allocations = [];

    private nint Allocate(int bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes);
        allocations.Add(pointer);
        return pointer;
    }

    private nint Utf8(string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("播放器参数包含无效字符。");
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        // 使用不同分配器的字符串独立释放。
        strings.Add(pointer);
        return pointer;
    }
    private readonly List<nint> strings = [];

    public MpvNode Build(MpvValue value)
    {
        switch (value)
        {
            case MpvValue.Text text: return new() { Format = MpvFormat.String, Pointer = Utf8(text.Value) };
            case MpvValue.WholeNumber integer: return new() { Format = MpvFormat.Int64, Int64 = integer.Value };
            case MpvValue.Number number: return new() { Format = MpvFormat.Double, Double = number.Value };
            case MpvValue.Flag flag: return new() { Format = MpvFormat.Flag, Flag = flag.Value ? 1 : 0 };
            case MpvValue.Array array:
                return BuildList(array.Values, null);
            case MpvValue.Map map:
                return BuildList(map.Values.Values.ToArray(), map.Values.Keys.ToArray());
            default: throw new ArgumentException("不支持的播放器参数类型。", nameof(value));
        }
    }

    private MpvNode BuildList(IReadOnlyList<MpvValue?> values, string[]? keys)
    {
        var list = (MpvNodeList*)Allocate(sizeof(MpvNodeList));
        list->Count = values.Count;
        list->Values = Allocate(Math.Max(1, values.Count) * sizeof(MpvNode));
        list->Keys = keys is null ? 0 : Allocate(Math.Max(1, keys.Length) * sizeof(nint));
        for (var i = 0; i < values.Count; i++)
        {
            ((MpvNode*)list->Values)[i] = values[i] is { } value ? Build(value) : default;
            if (keys is not null) ((nint*)list->Keys)[i] = Utf8(keys[i]);
        }
        return new() { Format = keys is null ? MpvFormat.NodeArray : MpvFormat.NodeMap, Pointer = (nint)list };
    }

    public void Dispose()
    {
        foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
        foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        strings.Clear();
        allocations.Clear();
    }
}
