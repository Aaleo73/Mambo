using System.Buffers.Binary;
using System.Text;

namespace Mambo.Player.Tests;

/// <summary>合成 PNG 视频与两条字幕的最小 Matroska；不使用真实媒体、字幕或服务器数据。</summary>
internal static class AssMatroskaFixture
{
    internal const string Header = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 384
        PlayResY: 288
        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,21,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,5,5,3,1

        """;
    internal const string EventFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    internal static string Dialogue(string text) => "Dialogue: 0,0:00:00.00,0:00:20.00,Default,,0,0,0,," + text + "\n";

    internal static byte[] Create(string text, bool broken)
    {
        var ebml = Element(0x1A45DFA3, Join(
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2)));
        var duration = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(duration, 20000);
        var info = Element(0x1549A966, Join(UInt(0x2AD7B1, 1000000), Element(0x4489, duration),
            Text(0x4D80, "Mambo test"), Text(0x5741, "Mambo test")));
        var privateData = broken ? Header + Dialogue(text) : Header + "[Events]\n" + EventFormat + "\n";
        var tracks = Element(0x1654AE6B, Join(
            Element(0xAE, Join(UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 1), UInt(0x23E383, 1000000000),
                Text(0x86, "V_PNG"), Element(0xE0, Join(UInt(0xB0, 16), UInt(0xBA, 16))))),
            Element(0xAE, Join(UInt(0xD7, 2), UInt(0x73C5, 2), UInt(0x83, 17), UInt(0x88, 1),
                Text(0x86, "S_TEXT/ASS"), Text(0x22B59C, "chi"), Text(0x536E, "合成双语 ASS"), Text(0x63A2, privateData))),
            Element(0xAE, Join(UInt(0xD7, 3), UInt(0x73C5, 3), UInt(0x83, 17), UInt(0x88, 0),
                Text(0x86, "S_TEXT/UTF8"), Text(0x22B59C, "chi"), Text(0x536E, "合成 SRT")))));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAEUlEQVR4nGNgGAWjYBQwQAEAAxAAAXyL/2UAAAAASUVORK5CYII=");
        var blocks = new List<byte[]> { UInt(0xE7, 0), SubtitleBlock(3, "正常字幕") };
        if (!broken) blocks.Add(SubtitleBlock(2, "0,0,Default,,0,0,0,," + text));
        for (var i = 0; i < 20; i++)
        {
            var block = new byte[4 + png.Length];
            block[0] = 0x81;
            BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(1), (short)(i * 1000));
            block[3] = 0x80;
            png.CopyTo(block, 4);
            blocks.Add(Element(0xA3, block));
        }
        return Join(ebml, Element(0x18538067, Join(info, tracks, Element(0x1F43B675, Join(blocks.ToArray())))));
    }

    private static byte[] SubtitleBlock(int track, string text) => Element(0xA0,
        Join(Element(0xA1, Join([(byte)(0x80 | track), 0, 0, 0], Encoding.UTF8.GetBytes(text))), UInt(0x9B, 20000)));
    private static byte[] Text(uint id, string text) => Element(id, Encoding.UTF8.GetBytes(text));
    private static byte[] UInt(uint id, ulong number) => Element(id, Bytes(number));

    private static byte[] Element(uint id, byte[] data)
    {
        var length = (ulong)data.Length;
        var width = 1;
        while (length >= (1UL << (width * 7)) - 1) width++;
        var encoded = Bytes(length | (1UL << (width * 7)), width);
        return Join(Bytes(id), encoded, data);
    }

    private static byte[] Bytes(ulong number, int width = 0)
    {
        if (width == 0) { width = 1; while (width < 8 && number >= (1UL << (width * 8))) width++; }
        var data = new byte[width];
        for (var i = width - 1; i >= 0; i--) { data[i] = (byte)number; number >>= 8; }
        return data;
    }

    private static byte[] Join(params byte[][] parts)
    {
        var joined = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(joined, offset); offset += part.Length; }
        return joined;
    }
}
