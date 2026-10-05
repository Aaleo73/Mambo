using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Xunit;
using NativeValue = Mambo.Player.LibMpv.MpvValue;

namespace Mambo.Player.Tests;

[Collection("Video quality GPU")]
public sealed class VideoQualityMeasurementTests
{
    // 只记录、不设门槛：显卡的频率状态会让绝对时间波动，同一帧内各类 pass 的占比才可比。
    // 片源是 lavfi 合成画面，数字不含真实片源的解码开销。
    [Fact]
    public async Task RecordsCompileAndRenderCostOfEachModeOnThisMachine()
    {
        var directory = Environment.GetEnvironmentVariable("MAMBO_QUALITY_REPORT");
        Assert.SkipUnless(OperatingSystem.IsWindows() && !string.IsNullOrEmpty(directory) &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll")),
            "设置 MAMBO_QUALITY_REPORT=<目录> 后记录本机三种画质的冷编译、切换与每帧耗时。");
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(directory!);
        var report = new StringBuilder("| 片源 → 显示 | 模式 | 冷编译 ms | 切换 ms | 每帧 ms | 其中 Mambo/Anime4K/mpv | 丢帧 | 延迟帧 |\n|---|---|---|---|---|---|---|---|\n");
        var caches = new List<string>();
        try
        {
            foreach (var (sourceWidth, sourceHeight, width, height) in new[]
            {
                (1920, 1080, 3840, 2160), (1920, 1080, 2560, 1440), (1920, 1080, 1920, 1080), (3840, 2160, 3840, 2160), (3840, 2160, 2560, 1440),
            })
            {
                async Task<LibMpvEngine> StartAsync(string? cache)
                {
                    var options = new Dictionary<string, string> { ["hwdec"] = "no" };
                    if (cache is not null) options["gpu-shader-cache-dir"] = cache;
                    var engine = await LibMpvEngine.CreateAsync(width, height, enableAudio: false, optionOverrides: options, cancellationToken: token);
                    await engine.PrepareVideoQualityAsync(VideoQualityMode.Standard, token);
                    await engine.LoadAsync($"av://lavfi:testsrc2=size={sourceWidth}x{sourceHeight}:rate=24,format=yuv420p10le", LoadMode.Replace, [], token);
                    await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
                    // 先让标准画质自己的着色器就绪，冷编译只计入切换带来的部分。
                    await Task.Delay(1500, token);
                    return engine;
                }
                var cold = new Dictionary<VideoQualityMode, long>();
                foreach (var mode in new[] { VideoQualityMode.Clear, VideoQualityMode.Anime })
                {
                    var cache = Path.Combine(Path.GetTempPath(), "mambo-quality-cache-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(cache);
                    caches.Add(cache);
                    await using var fresh = await StartAsync(cache);
                    var watch = Stopwatch.StartNew();
                    await fresh.ApplyVideoQualityAsync(mode, token);
                    cold[mode] = watch.ElapsedMilliseconds;
                }
                await using var engine = await StartAsync(null);
                foreach (var mode in new[] { VideoQualityMode.Standard, VideoQualityMode.Clear, VideoQualityMode.Anime })
                {
                    var watch = Stopwatch.StartNew();
                    await engine.ApplyVideoQualityAsync(mode, token);
                    var applied = watch.ElapsedMilliseconds;
                    var dropped = Count(engine, "frame-drop-count");
                    var delayed = Count(engine, "vo-delayed-frame-count");
                    await Task.Delay(4000, token);
                    var passes = engine.Core.GetProperty("vo-passes") is NativeValue.Map map && map.Values.GetValueOrDefault("fresh") is NativeValue.Array list
                        ? list.Values.OfType<NativeValue.Map>().Select(pass => (
                            Description: pass.Values.GetValueOrDefault("desc") is NativeValue.Text text ? text.Value : "",
                            Milliseconds: pass.Values.GetValueOrDefault("avg") is NativeValue.WholeNumber average ? average.Value / 1e6 : 0)).ToArray()
                        : [];
                    double Sum(Func<string, bool> match) => passes.Where(pass => match(pass.Description)).Sum(pass => pass.Milliseconds);
                    var upstream = Sum(description => description.Contains("Anime4K", StringComparison.Ordinal));
                    var own = Sum(description => description.Contains("Mambo ", StringComparison.Ordinal)) - upstream;
                    var total = passes.Sum(pass => pass.Milliseconds);
                    report.Append(CultureInfo.InvariantCulture, $"| {sourceWidth}x{sourceHeight} → {width}x{height} | {mode} | ");
                    report.Append(cold.TryGetValue(mode, out var compile) ? compile.ToString(CultureInfo.InvariantCulture) : "–");
                    report.Append(CultureInfo.InvariantCulture, $" | {applied} | {total:F2} | {own:F2} / {upstream:F2} / {total - own - upstream:F2} | ");
                    report.Append(CultureInfo.InvariantCulture, $"{Count(engine, "frame-drop-count") - dropped} | {Count(engine, "vo-delayed-frame-count") - delayed} |\n");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory!, "video-quality-measurements.md"), report.ToString(), token);
        }
        finally
        {
            foreach (var cache in caches)
                try { Directory.Delete(cache, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static long Count(LibMpvEngine engine, string property) =>
        engine.Core.GetProperty(property) is NativeValue.WholeNumber count ? count.Value : 0;
}
