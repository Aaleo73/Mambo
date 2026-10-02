using System.Runtime.InteropServices;

namespace Mambo.Player.LibMpv;

internal static unsafe class MpvNodeReader
{
    public static MpvValue? Read(MpvNode node, int depth = 0)
    {
        if (depth > 16) throw new InvalidOperationException("播放器属性嵌套超过限制。");
        return node.Format switch
        {
            MpvFormat.String => new MpvValue.Text(Marshal.PtrToStringUTF8(node.Pointer) ?? ""),
            MpvFormat.Int64 => new MpvValue.WholeNumber(node.Int64),
            MpvFormat.Double => new MpvValue.Number(node.Double),
            MpvFormat.Flag => new MpvValue.Flag(node.Flag != 0),
            MpvFormat.NodeArray or MpvFormat.NodeMap => ReadList(node, depth),
            _ => null,
        };
    }

    private static MpvValue ReadList(MpvNode node, int depth)
    {
        var list = *(MpvNodeList*)node.Pointer;
        if (list.Count < 0 || list.Count > 100_000) throw new InvalidOperationException("播放器属性列表大小无效。");
        var values = (MpvNode*)list.Values;
        if (node.Format == MpvFormat.NodeArray)
        {
            var array = new MpvValue?[list.Count];
            for (var i = 0; i < array.Length; i++) array[i] = Read(values[i], depth + 1);
            return new MpvValue.Array(array);
        }
        var map = new Dictionary<string, MpvValue?>(StringComparer.Ordinal);
        var keys = (nint*)list.Keys;
        for (var i = 0; i < list.Count; i++)
            map[Marshal.PtrToStringUTF8(keys[i]) ?? ""] = Read(values[i], depth + 1);
        return new MpvValue.Map(map);
    }

    public static MpvValue? ReadProperty(MpvEventProperty property) => property.Format switch
    {
        MpvFormat.String => new MpvValue.Text(Marshal.PtrToStringUTF8(*(nint*)property.Data) ?? ""),
        MpvFormat.Int64 => new MpvValue.WholeNumber(*(long*)property.Data),
        MpvFormat.Double => new MpvValue.Number(*(double*)property.Data),
        MpvFormat.Flag => new MpvValue.Flag(*(int*)property.Data != 0),
        MpvFormat.Node => Read(*(MpvNode*)property.Data),
        _ => null,
    };
}
