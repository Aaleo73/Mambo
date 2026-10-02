using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>Generates gradient/title BMP images entirely in managed code, without fonts or external art.</summary>
public sealed class FakeImageService(DemoCatalog catalog, FakeOperation operation) : IImageService
{
    public async Task<ReadOnlyMemory<byte>> FetchAsync(ImageRef image, int pixelWidth,
        ImagePriority priority = ImagePriority.Visible, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (pixelWidth <= 0)
            throw new AppException(new AppError(AppErrorKind.Contract, "demo.image.size", "图片宽度必须大于零。", false));
        await operation.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var item = catalog.Find(image.ItemId);
        var title = item?.Name ?? catalog.AllItems.SelectMany(candidate => candidate.People)
            .FirstOrDefault(person => person.Id == image.ItemId)?.Name ?? "演示图片";
        var portrait = image.Kind == ImageKind.Primary && item?.Kind is not (MediaKind.Episode or MediaKind.Video);
        // Keep even the largest generated poster below the real image pipeline's 16 MB per-entry limit.
        var width = Math.Clamp(pixelWidth, 16, portrait ? 960 : 2560);
        var height = image.Kind == ImageKind.Logo ? Math.Max(16, width / 5) :
            portrait ? width * 3 / 2 : Math.Max(16, width * 9 / 16);
        return await Task.Run(() => CreateBitmap(image, title, width, height, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateBitmap(ImageRef image, string title, int width, int height, CancellationToken cancellationToken)
    {
        var stride = (width * 3 + 3) & ~3;
        var pixels = new byte[54 + stride * height];
        pixels[0] = (byte)'B';
        pixels[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(2), pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(34), stride * height);
        var color = SHA256.HashData(Encoding.UTF8.GetBytes($"{image.ItemId}|{image.Kind}|{image.Tag}|{image.Index}"));
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var t = ((double)x / width + (double)y / height) / 2;
                var shade = 1 - 0.65 * Math.Clamp(((double)y / height - 0.45) / 0.55, 0, 1);
                var offset = 54 + (height - 1 - y) * stride + x * 3;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + 2 - channel] = (byte)(((60 + color[channel] % 135) * (1 - t) +
                        (35 + color[channel + 3] % 120) * t) * shade);
            }
        }
        var displayTitle = ExpandUnsupported(title);
        var units = displayTitle.Sum(character => Glyph(character).Width + 1);
        var scale = Math.Max(1, Math.Min(width / 90 + 1, (width - width / 8) / Math.Max(1, units)));
        var xOrigin = Math.Max(4, (width - units * scale) / 2);
        var yOrigin = Math.Clamp((int)(height * 0.68), 2, Math.Max(2, height - 12 * scale));
        DrawText(pixels, width, height, stride, displayTitle, xOrigin, yOrigin, scale);
        if (width >= 160 && height >= 120)
            DrawText(pixels, width, height, stride, "MAMBO DEMO", Math.Max(8, width / 12), height - Math.Max(18, height / 14),
                Math.Max(1, width / 320));
        cancellationToken.ThrowIfCancellationRequested();
        return pixels;
    }

    private static string ExpandUnsupported(string title)
    {
        var result = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            if (rune.IsAscii || rune.Value <= char.MaxValue && Hanzi.ContainsKey((char)rune.Value))
                result.Append(rune.ToString());
            else result.Append("U+").Append(rune.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void DrawText(byte[] pixels, int width, int height, int stride, string title, int x, int y, int scale)
    {
        foreach (var character in title)
        {
            var (rows, glyphWidth) = Glyph(character);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < glyphWidth; column++)
                    if (rows[row][column] == '#')
                        for (var dy = 0; dy < scale; dy++)
                            for (var dx = 0; dx < scale; dx++)
                            {
                                var px = x + column * scale + dx;
                                var py = y + row * scale + dy;
                                if (px < 0 || px >= width || py < 0 || py >= height) continue;
                                var offset = 54 + (height - 1 - py) * stride + px * 3;
                                pixels[offset] = 242;
                                pixels[offset + 1] = 246;
                                pixels[offset + 2] = 250;
                            }
            x += (glyphWidth + 1) * scale;
            if (x >= width) break;
        }
    }

    private static (string[] Rows, int Width) Glyph(char character)
    {
        if (Hanzi.TryGetValue(character, out var hanzi)) return (hanzi, 9);
        if (Latin.TryGetValue(char.ToUpperInvariant(character), out var latin)) return (latin, 5);
        return (Latin[' '], 5);
    }

    // Original compact pixel lettering for the finite demo catalog; no embedded or copied font assets.
    private static readonly Dictionary<char, string[]> Hanzi = new()
    {
        ['电'] = Rows("....#..../.#######./.#..#..#./.#######./.#..#..#./.#######./....#..../....#..#./....####."),
        ['影'] = Rows("#####...#/#...#..#./#####.#../..#...#.#/#####..#./..#.#.#../#.#.#...#/..#...#.#/#.#....#."),
        ['星'] = Rows(".#######./.#.....#./.#######./.#.....#./.#######./..#.#..../.#######./....#..../#########"),
        ['际'] = Rows("###.#####/#.#...#../##..#####/#.#...#../#.#.#.#.#/##..#.#.#/#.....#../#....##../#........"),
        ['回'] = Rows("#########/#.......#/#.#####.#/#.#...#.#/#.#...#.#/#.#####.#/#.......#/#.......#/#########"),
        ['声'] = Rows("....#..../#########/....#..../.#######./........./.#######./.#..#..#./.#######./#........"),
        ['山'] = Rows("....#..../....#..../.#..#..#./.#..#..#./.#..#..#./.#..#..#./.#..#..#./.#######./........."),
        ['海'] = Rows("#...#..../..#.#####/.#..#..../...#####./#..#.#.#./...#####./.#.#.#.#./#..#####./......#.."),
        ['旅'] = Rows(".#...#.../#####.###/..#...#../.###.###./.#.#.#.#./.#.#.#.#./.#.#.##../#..#.#.#./..##.#..#"),
        ['途'] = Rows("#...#..../..##.##../.#..#..#./....#..../..#######/....#..../.##.#.#.#/..#.#..../.########"),
        ['深'] = Rows("#..#####./...#...#./.#..#.#../.....#.../#..#####./.....#.../.#.#####./#..#.#.#./..#..#..#"),
        ['来'] = Rows("....#..../#########/..#.#.#../...###.../#########/...###.../..#.#.#../.#..#..#./#...#...#"),
        ['信'] = Rows("..#...#../.#..#####/##......./.#..#####/.#......./.#..#####/.#..#...#/.#..#...#/.#..#####"),
        ['第'] = Rows(".#....#../.###..###/#.#..#.#./.#######./......#../.#######./.#..#..#./.#######./#...#..#."),
        ['集'] = Rows("..#.#..../.#..#..../########./.#..#..../.#######./.#..#..../#########/...###.../.##.#.##."),
        ['季'] = Rows("..#####../....#..../#########/...###.../.##.#.##./..#####../.....#.../#########/....##..."),
        ['视'] = Rows(".#..#####/####.#..#/..#..#..#/.##..#..#/#.#..#..#/.#...#..#/.#...#.../.#..#..#./.#.#...##"),
        ['频'] = Rows("..#.#####/.###..#../..#..###./#####...#/..#.#####/.##.#...#/..#.#.#.#/.#..#.#.#/#....#..#"),
        ['演'] = Rows("#....#.../...#####./.#.#...#./.....#.../#..#####./...#.#.#./.#.#####./#...#.#../...#...#."),
        ['示'] = Rows(".#######./........./#########/....#..../..#.#.#../..#.#.#../.#..#..#./#...#...#/...##...."),
        ['图'] = Rows("#########/#..#....#/#.#####.#/#.#...#.#/#..#.#..#/#...#...#/#..#.#..#/#.#...#.#/#########"),
        ['片'] = Rows("..#..#.../..#..#.../..#######/..#....../..#....../..#####../..#...#../.#....#../#.....#.."),
        ['短'] = Rows(".#..#####/.###.#..#/..#..#..#/#####.###/..#.....#/.###.###./..#..#.#./.#.#.#.#./#...#####"),
        ['员'] = Rows(".#######./.#.....#./.#######./........./.#######./.#.....#./.#..#..#./...#.#.../.##...##."),
        ['导'] = Rows(".#######./.......#./.#######./.#......./.#######./......#../#########/..#...#../......##."),
        ['人'] = Rows("....#..../....#..../....#..../...#.#.../...#.#.../..#...#../..#...#../.#.....#./#.......#"),
        ['一'] = Rows("........./........./........./........./#########/........./........./........./........."),
        ['二'] = Rows("........./........./.#######./........./........./........./#########/........./........."),
        ['三'] = Rows("........./.#######./........./........./..#####../........./........./#########/........."),
    };

    private static readonly Dictionary<char, string[]> Latin = new()
    {
        [' '] = Rows("...../...../...../...../...../...../....."),
        ['0'] = Rows(".###./#...#/#..##/#.#.#/##..#/#...#/.###."),
        ['1'] = Rows("..#../.##../..#../..#../..#../..#../.###."),
        ['2'] = Rows(".###./#...#/....#/...#./..#../.#.../#####"),
        ['3'] = Rows("####./....#/....#/.###./....#/....#/####."),
        ['4'] = Rows("...#./..##./.#.#./#..#./#####/...#./...#."),
        ['5'] = Rows("#####/#..../#..../####./....#/....#/####."),
        ['6'] = Rows(".###./#..../#..../####./#...#/#...#/.###."),
        ['7'] = Rows("#####/....#/...#./..#../.#.../.#.../.#..."),
        ['8'] = Rows(".###./#...#/#...#/.###./#...#/#...#/.###."),
        ['9'] = Rows(".###./#...#/#...#/.####/....#/....#/.###."),
        ['A'] = Rows(".###./#...#/#...#/#####/#...#/#...#/#...#"),
        ['B'] = Rows("####./#...#/#...#/####./#...#/#...#/####."),
        ['C'] = Rows(".####/#..../#..../#..../#..../#..../.####"),
        ['D'] = Rows("####./#...#/#...#/#...#/#...#/#...#/####."),
        ['E'] = Rows("#####/#..../#..../####./#..../#..../#####"),
        ['F'] = Rows("#####/#..../#..../####./#..../#..../#...."),
        ['G'] = Rows(".####/#..../#..../#.###/#...#/#...#/.###."),
        ['H'] = Rows("#...#/#...#/#...#/#####/#...#/#...#/#...#"),
        ['I'] = Rows("#####/..#../..#../..#../..#../..#../#####"),
        ['J'] = Rows("..###/...#./...#./...#./...#./#..#./.##.."),
        ['K'] = Rows("#...#/#..#./#.#../##.../#.#../#..#./#...#"),
        ['L'] = Rows("#..../#..../#..../#..../#..../#..../#####"),
        ['M'] = Rows("#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#"),
        ['N'] = Rows("#...#/##..#/#.#.#/#..##/#...#/#...#/#...#"),
        ['O'] = Rows(".###./#...#/#...#/#...#/#...#/#...#/.###."),
        ['P'] = Rows("####./#...#/#...#/####./#..../#..../#...."),
        ['Q'] = Rows(".###./#...#/#...#/#...#/#.#.#/#..#./.##.#"),
        ['R'] = Rows("####./#...#/#...#/####./#.#../#..#./#...#"),
        ['S'] = Rows(".####/#..../#..../.###./....#/....#/####."),
        ['T'] = Rows("#####/..#../..#../..#../..#../..#../..#.."),
        ['U'] = Rows("#...#/#...#/#...#/#...#/#...#/#...#/.###."),
        ['V'] = Rows("#...#/#...#/#...#/#...#/#...#/.#.#./..#.."),
        ['W'] = Rows("#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#"),
        ['X'] = Rows("#...#/#...#/.#.#./..#../.#.#./#...#/#...#"),
        ['Y'] = Rows("#...#/#...#/.#.#./..#../..#../..#../..#.."),
        ['Z'] = Rows("#####/....#/...#./..#../.#.../#..../#####"),
        ['+'] = Rows("...../..#../..#../#####/..#../..#../....."),
        ['-'] = Rows("...../...../...../#####/...../...../....."),
        ['.'] = Rows("...../...../...../...../...../.##../.##.."),
    };

    private static string[] Rows(string rows) => rows.Split('/');
}
