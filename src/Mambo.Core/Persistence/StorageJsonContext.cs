using System.Text.Json.Serialization;
using Mambo.Core.Session;
using Mambo.Core.Contracts;

namespace Mambo.Core.Persistence;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(SessionSecret))]
[JsonSerializable(typeof(SubtitleStyleSettings))]
[JsonSerializable(typeof(TrackPreferenceRecord))]
public sealed partial class StorageJsonContext : JsonSerializerContext;
