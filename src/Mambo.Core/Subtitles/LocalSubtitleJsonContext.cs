using System.Text.Json.Serialization;

namespace Mambo.Core.Subtitles;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LocalSubtitleIndexDocument))]
internal sealed partial class LocalSubtitleJsonContext : JsonSerializerContext;
