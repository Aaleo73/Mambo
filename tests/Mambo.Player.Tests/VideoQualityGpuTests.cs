using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Player.LibMpv;
using Xunit;
using NativeValue = Mambo.Player.LibMpv.MpvValue;

namespace Mambo.Player.Tests;

[CollectionDefinition("Video quality GPU", DisableParallelization = true)]
public sealed class VideoQualityGpuGroup;

[Collection("Video quality GPU")]
public sealed class VideoQualityGpuTests
{
    private static readonly string[] HdrTransfers = ["pq", "hlg"];
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BaselinePropertiesAreAcceptedByNativeMpv(bool headless)
    {
        RequireGpu();
        await using var core = new MpvCore(640, 360, headless: headless, enableAudio: false);
        var properties = new Dictionary<string, NativeValue>
        {
            ["glsl-shaders"] = new NativeValue.Array([]),
            ["glsl-shader-opts"] = new NativeValue.Map(new Dictionary<string, NativeValue?>()),
            ["scale"] = new NativeValue.Text("lanczos"),
            ["scale-antiring"] = new NativeValue.Number(0),
            ["dscale"] = new NativeValue.Text("hermite"),
            ["cscale"] = new NativeValue.Text(""),
        };
        foreach (var (name, value) in properties)
        {
            var failure = await Record.ExceptionAsync(() => core.SetPropertyAsync(name, value, TestContext.Current.CancellationToken));
            Assert.True(failure is null, name + ": " + failure?.GetType().Name + " " + failure?.Message);
        }
    }

    private static void RequireGpu() => Assert.SkipUnless(
        OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("MAMBO_TEST_GPU") == "1" &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll")),
        "设置 MAMBO_TEST_GPU=1 后运行真实 D3D11 composition 着色器验证。");

    [Theory]
    [InlineData("bt709", false)]
    [InlineData("bt709", true)]
    [InlineData("smpte2084", false)]
    [InlineData("arib-std-b67", false)]
    [InlineData("smpte2084", true)]
    [InlineData("arib-std-b67", true)]
    [InlineData("bt709", false, 256, 144)]
    [InlineData("bt709", false, 128, 72)]
    [InlineData("smpte2084", true, 256, 144)]
    [InlineData("arib-std-b67", false, 128, 72)]
    [InlineData("smpte2084", false, 128, 72)]
    [InlineData("smpte2084", true, 128, 72)]
    public async Task RealGpuCompilesPresetsAndSwitchesWhilePaused(string transfer, bool hdrOutput, int width = 640, int height = 360)
    {
        RequireGpu();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var engine = await LibMpvEngine.CreateAsync(width, height, enableAudio: false,
            optionOverrides: new Dictionary<string, string> { ["hwdec"] = "no", ["pause"] = "yes" }, cancellationToken: token);
        engine.ApplyHdr(hdrOutput);
        await engine.PrepareVideoQualityAsync(VideoQualityMode.Standard, token);
        var primaries = transfer == "bt709" ? "bt709" : "bt2020";
        var source = $"av://lavfi:testsrc2=size=256x144:rate=24,format=yuv420p10le,setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={(transfer == "bt709" ? "bt709" : "bt2020nc")}";
        await engine.LoadAsync(source, LoadMode.Replace, [], token);
        await foreach (var item in engine.Events.ReadAllAsync(token))
            if (item is EngineEvent.PlaybackRestart) break;
        var position = Number(engine.Core.GetProperty("time-pos"));
        var sourceParameters = Assert.IsType<NativeValue.Map>(engine.Core.GetProperty("video-params"));
        if (transfer != "bt709")
            Assert.Equal(transfer == "smpte2084" ? "pq" : "hlg", Assert.IsType<NativeValue.Text>(sourceParameters.Values["gamma"]).Value);
        var initialShaderFailures = engine.Core.ShaderFailureVersion;
        foreach (var mode in new[] { VideoQualityMode.Clear, VideoQualityMode.Anime, VideoQualityMode.Standard, VideoQualityMode.Clear, VideoQualityMode.Standard })
        {
            var applyError = await Record.ExceptionAsync(async () => await engine.ApplyVideoQualityAsync(mode, token));
            Assert.True(applyError is null, $"Mode {mode}, stage {engine.Core.ShaderFailureStage}, error {applyError?.Message}");
            await Task.Delay(100, token);
            Assert.True(initialShaderFailures == engine.Core.ShaderFailureVersion,
                $"Mode {mode}, failures before {initialShaderFailures}, after {engine.Core.ShaderFailureVersion}, stage {engine.Core.ShaderFailureStage}");
            Assert.Equal(position, Number(engine.Core.GetProperty("time-pos")), 5);
            Assert.True(Assert.IsType<NativeValue.Flag>(engine.Core.GetProperty("pause")).Value);
            Assert.Equal(mode == VideoQualityMode.Standard ? "lanczos" : "ewa_lanczossharp",
                Assert.IsType<NativeValue.Text>(engine.Core.GetProperty("scale")).Value);
            var shaders = Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders"));
            Assert.Equal(mode == VideoQualityMode.Standard ? 0 : 1, shaders.Values.Count);
            var targetParameters = Assert.IsType<NativeValue.Map>(engine.Core.GetProperty("video-target-params"));
            var targetGamma = Assert.IsType<NativeValue.Text>(targetParameters.Values["gamma"]).Value;
            if (hdrOutput) Assert.Equal("pq", targetGamma);
            else Assert.DoesNotContain(targetGamma, HdrTransfers);
        }
        Assert.True(initialShaderFailures == engine.Core.ShaderFailureVersion,
            $"Shader failures before presets: {initialShaderFailures}, after presets: {engine.Core.ShaderFailureVersion}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidResourceKeepsLastConfirmedModeAndCancellationDoesNotLeaveHalfPreset(bool missing)
    {
        RequireGpu();
        var token = TestContext.Current.CancellationToken;
        await using var engine = await LibMpvEngine.CreateAsync(640, 360, enableAudio: false,
            optionOverrides: new Dictionary<string, string> { ["pause"] = "yes" }, cancellationToken: token);
        await engine.LoadAsync("av://lavfi:testsrc2=size=256x144:rate=24", LoadMode.Replace, [], token);
        await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Clear, token);
        var asset = Path.Combine(AppContext.BaseDirectory, "mpv", "shaders", "Mambo_Anime.glsl");
        var original = await File.ReadAllBytesAsync(asset, token);
        try
        {
            if (missing) File.Delete(asset);
            else await File.WriteAllTextAsync(asset, "// damaged local fixture", token);
            await Assert.ThrowsAsync<AppException>(async () => await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, token));
            var shaders = Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders"));
            Assert.EndsWith("Mambo_Clear.glsl", Assert.IsType<NativeValue.Text>(Assert.Single(shaders.Values)).Value, StringComparison.Ordinal);
            Assert.True(Assert.IsType<NativeValue.Flag>(engine.Core.GetProperty("pause")).Value);
        }
        finally { await File.WriteAllBytesAsync(asset, original, CancellationToken.None); }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, cancelled.Token));
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Standard, token);
        Assert.Empty(Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders")).Values);
    }

    [Fact]
    public async Task RealShaderCompilationFailureRestoresThePreviousRenderedPreset()
    {
        RequireGpu();
        var token = TestContext.Current.CancellationToken;
        var brokenShader = Path.Combine(Path.GetTempPath(), "mambo-quality-" + Guid.NewGuid().ToString("N") + ".glsl");
        try
        {
            await File.WriteAllTextAsync(brokenShader,
                "//!HOOK MAIN\n//!BIND HOOKED\n//!DESC Mambo Anime invalid compilation fixture\nvec4 hook() { return undefined_test_symbol; }\n", token);
            await using var engine = await LibMpvEngine.CreateAsync(640, 360, enableAudio: false,
                optionOverrides: new Dictionary<string, string> { ["pause"] = "yes" }, cancellationToken: token);
            await engine.LoadAsync("av://lavfi:testsrc2=size=256x144:rate=24", LoadMode.Replace, [], token);
            await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
            await using var controller = new VideoQualityController(engine.Core, mode => mode switch
            {
                VideoQualityMode.Anime => [brokenShader],
                VideoQualityMode.Clear => [Path.Combine(AppContext.BaseDirectory, "mpv", "shaders", "Mambo_Clear.glsl")],
                _ => [],
            });
            await controller.ApplyAsync(VideoQualityMode.Clear, token);
            var failures = engine.Core.ShaderFailureVersion;
            var position = Number(engine.Core.GetProperty("time-pos"));
            var error = await Assert.ThrowsAsync<AppException>(() => controller.ApplyAsync(VideoQualityMode.Anime, token));
            Assert.Equal("player.video_quality_failed", error.Error.Code);
            Assert.True(engine.Core.ShaderFailureVersion > failures);
            var shaders = Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders"));
            Assert.EndsWith("Mambo_Clear.glsl", Assert.IsType<NativeValue.Text>(Assert.Single(shaders.Values)).Value, StringComparison.Ordinal);
            Assert.Equal(position, Number(engine.Core.GetProperty("time-pos")), 5);
            Assert.True(Assert.IsType<NativeValue.Flag>(engine.Core.GetProperty("pause")).Value);
            // Apply returns only after the rollback's Clear terminal pass has executed.
            await controller.ApplyAsync(VideoQualityMode.Standard, token);
            Assert.Empty(Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders")).Values);
        }
        finally { File.Delete(brokenShader); }
    }

    [Fact]
    public async Task ShaderFailureAfterConfirmationClearsTheEnhancementAndNotifies()
    {
        RequireGpu();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var token = timeout.Token;
        var lateShader = Path.Combine(Path.GetTempPath(), "mambo-quality-" + Guid.NewGuid().ToString("N") + ".glsl");
        try
        {
            // The second pass is compiled only once the output is wide enough, like an upscaler that first runs after a resize.
            await File.WriteAllTextAsync(lateShader,
                "//!HOOK MAIN\n//!BIND HOOKED\n//!DESC Mambo Anime restore HDR source and bounded detail gain\nvec4 hook() { return HOOKED_tex(HOOKED_pos); }\n\n" +
                "//!HOOK MAIN\n//!BIND HOOKED\n//!WHEN OUTPUT.w 1000 >\n//!DESC Mambo Anime late compilation fixture\nvec4 hook() { return undefined_test_symbol; }\n", token);
            await using var engine = await LibMpvEngine.CreateAsync(mode => mode == VideoQualityMode.Anime ? [lateShader] : [],
                640, 360, headless: false, enableAudio: false,
                new Dictionary<string, string> { ["pause"] = "yes" }, token);
            await engine.LoadAsync("av://lavfi:testsrc2=size=256x144:rate=24", LoadMode.Replace, [], token);
            await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
            await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, token);
            var failures = engine.Core.ShaderFailureVersion;
            Assert.Single(Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders")).Values);
            engine.SetCompositionSize(1280, 720);
            await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.VideoQualityLost) break;
            Assert.True(engine.Core.ShaderFailureVersion > failures);
            Assert.Empty(Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders")).Values);
            Assert.Equal("lanczos", Assert.IsType<NativeValue.Text>(engine.Core.GetProperty("scale")).Value);
            Assert.True(Assert.IsType<NativeValue.Flag>(engine.Core.GetProperty("pause")).Value);
        }
        finally { File.Delete(lateShader); }
    }

    [Fact]
    public async Task ReapplyingTheWrittenPresetKeepsItAndSwitchingBetweenEnhancementsNeverPassesThroughStandard()
    {
        RequireGpu();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var engine = await LibMpvEngine.CreateAsync(640, 360, enableAudio: false,
            optionOverrides: new Dictionary<string, string> { ["hwdec"] = "no", ["pause"] = "yes" }, cancellationToken: token);
        // Prepared before the first frame, as the session does ahead of loading.
        await engine.PrepareVideoQualityAsync(VideoQualityMode.Anime, token);
        var source = "av://lavfi:testsrc2=size=256x144:rate=24";
        await engine.LoadAsync(source, LoadMode.Replace, [], token);
        await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
        using var sampling = new CancellationTokenSource();
        var sawStandard = false;
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                if (engine.Core.GetProperty("glsl-shaders") is NativeValue.Array { Values.Count: 0 }) sawStandard = true;
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);
        // The paused first frame was already rendered with the prepared preset; confirming must not need another one.
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, token);
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Clear, token);
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, token);
        // The next file in the same session keeps the mode: nothing is rewritten around the load.
        await engine.PrepareVideoQualityAsync(VideoQualityMode.Anime, token);
        await engine.LoadAsync(source, LoadMode.Replace, [], token);
        await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
        await engine.ApplyVideoQualityAsync(VideoQualityMode.Anime, token);
        await sampling.CancelAsync();
        await sampler;
        Assert.False(sawStandard);
        Assert.EndsWith("Mambo_Anime.glsl", Assert.IsType<NativeValue.Text>(Assert.Single(
            Assert.IsType<NativeValue.Array>(engine.Core.GetProperty("glsl-shaders")).Values)).Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1280, 720, "source-linear")]
    [InlineData(512, 288, "source-linear")]
    [InlineData(256, 144, "scaled-linear")]
    public async Task ClearSharpensOnceAtWhicheverOfSourceAndDisplayHasFewerPixels(int width, int height, string expectedPass)
    {
        RequireGpu();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var shots = Path.Combine(Path.GetTempPath(), "mambo-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shots);
        try
        {
            await using var engine = await LibMpvEngine.CreateAsync(width, height, enableAudio: false,
                optionOverrides: new Dictionary<string, string> { ["hwdec"] = "no", ["pause"] = "yes" }, cancellationToken: token);
            await engine.PrepareVideoQualityAsync(VideoQualityMode.Standard, token);
            async Task<(Image Standard, Image Clear)> CompareAsync(string content)
            {
                await engine.ApplyVideoQualityAsync(VideoQualityMode.Standard, token);
                await engine.LoadAsync($"av://lavfi:{content}=size=512x288:rate=24", LoadMode.Replace, [], token);
                await foreach (var item in engine.Events.ReadAllAsync(token)) if (item is EngineEvent.PlaybackRestart) break;
                var standard = await ScreenshotAsync(engine, Path.Combine(shots, content + "-standard.png"), token);
                await engine.ApplyVideoQualityAsync(VideoQualityMode.Clear, token);
                var passes = Assert.IsType<NativeValue.Map>(engine.Core.GetProperty("vo-passes")).Values.Values.OfType<NativeValue.Array>()
                    .SelectMany(list => list.Values.OfType<NativeValue.Map>())
                    .Select(pass => pass.Values.GetValueOrDefault("desc") is NativeValue.Text text ? text.Value : "")
                    .Where(description => description.Contains("Mambo Clear", StringComparison.Ordinal)).Distinct().ToArray();
                Assert.Contains(expectedPass, Assert.Single(passes), StringComparison.Ordinal);
                return (standard, await ScreenshotAsync(engine, Path.Combine(shots, content + "-clear.png"), token));
            }
            var texture = await CompareAsync("mandelbrot");
            var chart = await CompareAsync("testsrc2");
            Assert.Equal((width, height), (texture.Clear.Width, texture.Clear.Height));
            // Upscaling also swaps the scaler between the two modes, so only the shader's own effect is compared elsewhere.
            if (width > 512) return;
            // Measured on the locked libmpv: about 0.003 here, against 0.0009 when a 2x downscale follows the sharpening.
            Assert.InRange(RootMeanSquareDifference(texture.Standard, texture.Clear), 0.002, 0.02);
            Assert.InRange(LargestChangeOnFlatAreas(chart.Standard, chart.Clear), 0, 0.002);
        }
        finally { Directory.Delete(shots, recursive: true); }
    }

    private sealed record Image(int Width, int Height, int Channels, double[] Samples)
    {
        public double At(int x, int y, int channel) => Samples[(y * Width + x) * Channels + channel];
    }

    // The window screenshot is rendered through the same hooks and scalers as the displayed frame.
    private static async Task<Image> ScreenshotAsync(LibMpvEngine engine, string path, CancellationToken token)
    {
        await engine.Core.CommandAsync(new[] { "screenshot-to-file", path, "window" }.AsMemory(), token);
        var bytes = await File.ReadAllBytesAsync(path, token);
        int position = 8, width = 0, height = 0, depth = 0, type = 0;
        using var compressed = new MemoryStream();
        while (position < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
            var kind = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var data = bytes.AsSpan(position + 8, length);
            if (kind == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]); depth = data[8]; type = data[9]; }
            else if (kind == "IDAT") compressed.Write(data);
            position += 12 + length;
        }
        var channels = type switch { 2 => 3, 6 => 4, _ => throw new InvalidDataException("Unexpected screenshot colour type.") };
        var sample = depth / 8;
        var pixel = channels * sample;
        var stride = width * pixel;
        compressed.Position = 0;
        await using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[height * (stride + 1)];
        await inflate.ReadExactlyAsync(raw, token);
        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int left = x >= pixel ? pixels[y * stride + x - pixel] : 0;
                int above = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int corner = x >= pixel && y > 0 ? pixels[(y - 1) * stride + x - pixel] : 0;
                var predicted = filter switch
                {
                    0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2,
                    4 => Math.Abs(above - corner) <= Math.Abs(left - corner) && Math.Abs(above - corner) <= Math.Abs(left + above - 2 * corner) ? left
                        : Math.Abs(left - corner) <= Math.Abs(left + above - 2 * corner) ? above : corner,
                    _ => throw new InvalidDataException("Unexpected screenshot row filter."),
                };
                pixels[y * stride + x] = (byte)(raw[y * (stride + 1) + 1 + x] + predicted);
            }
        }
        var samples = new double[width * height * channels];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = sample == 1 ? pixels[index] / 255.0 : BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan(index * 2)) / 65535.0;
        return new(width, height, channels, samples);
    }

    private static double RootMeanSquareDifference(Image first, Image second)
    {
        double sum = 0;
        for (var y = 0; y < first.Height; y++)
            for (var x = 0; x < first.Width; x++)
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = first.At(x, y, channel) - second.At(x, y, channel);
                    sum += difference * difference;
                }
        return Math.Sqrt(sum / (first.Width * first.Height * 3));
    }

    // A pixel counts as flat when its 5x5 neighbourhood in the reference stays within one 10-bit step, which is what output dithering adds.
    private static double LargestChangeOnFlatAreas(Image reference, Image changed)
    {
        double largest = 0;
        var flatPixels = 0;
        for (var y = 2; y < reference.Height - 2; y++)
            for (var x = 2; x < reference.Width - 2; x++)
            {
                var flat = true;
                for (var channel = 0; channel < 3 && flat; channel++)
                {
                    double low = 1, high = 0;
                    for (var offsetY = -2; offsetY <= 2; offsetY++)
                        for (var offsetX = -2; offsetX <= 2; offsetX++)
                        {
                            var value = reference.At(x + offsetX, y + offsetY, channel);
                            low = Math.Min(low, value);
                            high = Math.Max(high, value);
                        }
                    flat = high - low < 1.5 / 1023;
                }
                if (!flat) continue;
                flatPixels++;
                for (var channel = 0; channel < 3; channel++)
                    largest = Math.Max(largest, Math.Abs(reference.At(x, y, channel) - changed.At(x, y, channel)));
            }
        Assert.True(flatPixels > 100, $"The chart has only {flatPixels} flat pixels to compare.");
        return largest;
    }

    private static double Number(NativeValue? value) => value switch
    {
        NativeValue.Number number => number.Value,
        NativeValue.WholeNumber number => number.Value,
        _ => 0,
    };
}
