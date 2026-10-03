using System.Text.Json;
using System.Text.Json.Serialization;
using Mambo.App.Composition;

namespace Mambo.App.Debug;

/// <summary>仅显式假模式的退出诊断；不输出异常消息或用户数据。</summary>
internal static class FakeLifetimeProbe
{
    private static string? reportPath;
    private static readonly FakeLifetimeReport Report = new();
    internal static bool IsActive => reportPath is not null;

    internal static void Initialize(string[] args)
    {
        if (!BackendServices.IsFakeMode(args, Environment.GetEnvironmentVariable("MAMBO_FAKE"))) return;
        var directory = Environment.GetEnvironmentVariable("MAMBO_FAKE_LIFETIME_REPORT_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        reportPath = Path.Combine(directory, "process-" + Environment.ProcessId + ".json");
        Mark("Entry");
    }

    internal static void Mark(string stage)
    {
        if (reportPath is null) return;
        Report.Stages.Add(stage);
        Save();
    }

    internal static void Record(Exception error)
    {
        if (reportPath is null) return;
        Report.ErrorKind = error.GetType().Name;
        Report.HResult = error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        Report.ErrorStack = error.StackTrace ?? "";
        Save();
    }

    private static void Save()
    {
        try { File.WriteAllText(reportPath!, JsonSerializer.Serialize(Report, FakeLifetimeJsonContext.Default.FakeLifetimeReport)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeLifetimeReport
{
    public List<string> Stages { get; set; } = [];
    public string ErrorKind { get; set; } = "";
    public string HResult { get; set; } = "";
    public string ErrorStack { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FakeLifetimeReport))]
internal sealed partial class FakeLifetimeJsonContext : JsonSerializerContext;
