using Mambo.Core.Networking;

namespace Mambo.Core.Playback;

public static class DeviceProfileFactory
{
    private static readonly string[] Formats = ["srt", "ass", "ssa", "sub", "vtt", "pgssub", "dvdsub", "dvbsub", "mov_text", "text", "ttml"];
    private static readonly HashSet<string> ExternalFormats = ["srt", "ass", "ssa", "sub", "vtt"];
    public static DeviceProfile Create() => new()
    {
        SubtitleProfiles = Formats.SelectMany(format => ExternalFormats.Contains(format) ?
            new[] { new SubtitleProfile(format, "External"), new SubtitleProfile(format, "Embed") } :
            new[] { new SubtitleProfile(format, "Embed") }).ToArray(),
    };
}
