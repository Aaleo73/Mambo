using System.Text.Json.Serialization;
using Mambo.Core.Session;

namespace Mambo.Core.Persistence;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(SessionSecret))]
public sealed partial class StorageJsonContext : JsonSerializerContext;
