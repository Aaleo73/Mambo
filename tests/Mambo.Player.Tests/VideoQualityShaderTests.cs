using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Mambo.Player.Tests;

public sealed class VideoQualityShaderTests
{
    // The scalar reference below checks the shader design's mathematical contract.
    // It is not a GLSL interpreter and does not claim GPU compilation or visual validation.
    [Fact]
    public void VendoredSourcesAndRuntimeMatchTheirHashes()
    {
        var root = FindShaderRoot();
        using var sourceLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "shaders.lock.json")));
        VerifyHashes(root, sourceLock.RootElement.GetProperty("upstream"));
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime", "manifest.json")));
        VerifyHashes(root, runtime.RootElement.GetProperty("files"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.00001)]
    [InlineData(0.18)]
    [InlineData(1.0)]
    [InlineData(8.0)]
    [InlineData(49.26108374384236)]
    public void HdrProxyRoundTripPreservesRadiance(double radiance)
    {
        var roundTrip = InverseProxy(Proxy(radiance));
        Assert.InRange(Math.Abs(roundTrip - radiance), 0, 1e-9 * Math.Max(1, radiance));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(4.926108374384236)]
    [InlineData(49.26108374384236)]
    public void CasPreservesBlackWhiteAndFlatPatches(double peak)
    {
        foreach (var value in new[] { 0.0, peak * 0.001, peak * 0.18, peak * 0.8, peak })
            Assert.Equal(value, Cas([value, value, value, value, value], peak));
    }

    [Fact]
    public void CasScalesWithSourceWhiteWithoutClippingHdrToSdr()
    {
        double[] neighborhood = [0.18, 0.2, 0.26, 0.21, 0.15];
        const double peak = 49.26108374384236;
        var sdr = Cas(neighborhood, 1);
        var hdr = Cas(neighborhood.Select(x => x * peak).ToArray(), peak);
        Assert.InRange(Math.Abs(hdr - sdr * peak), 0, 1e-10);
        Assert.True(hdr > 1.0);
    }

    [Fact]
    public void CasInvalidOrOutOfRangeNeighborPreservesCenter()
    {
        foreach (var invalid in new[] { -0.01, 2.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(0.18, Cas([invalid, 0.1, 0.18, 0.2, 0.3], 1));
    }

    [Fact]
    public void HdrAnimeKeepsFlatFieldsAndBlackRegardlessOfNetworkBias()
    {
        Assert.Equal(1, HdrGain(0.9, Proxy(8), 8, 0, 49.3));
        Assert.Equal(1, HdrGain(0.9, 0, 0, 1, 49.3));
        Assert.Equal(1, HdrGain(0.9, Proxy(1e-6), 1e-6, 1, 49.3));
    }

    [Fact]
    public void HdrAnimeBoundsDetailGainAndPreservesChannelRatios()
    {
        double[] color = [8, 2, 0.5];
        foreach (var processed in new[] { -10.0, 0.0, 0.6, 0.9, 10.0 })
        {
            var gain = HdrGain(processed, Proxy(8), 8, 1, 49.3);
            Assert.InRange(gain, 0.9, 1.1);
            var result = color.Select(x => x * gain).ToArray();
            Assert.Equal(color[0] / color[1], result[0] / result[1], 12);
            Assert.Equal(color[1] / color[2], result[1] / result[2], 12);
            Assert.True(result[0] > 1);
        }
    }

    [Fact]
    public void HdrAnimeReducesAddedGainAtSignalCeilingWithoutChannelClipping()
    {
        const double peak = 49.3;
        Assert.Equal(1, HdrGain(1, Proxy(peak), peak, 1, peak));
        var maximum = peak * 0.98;
        var gain = HdrGain(1, Proxy(maximum), maximum, 1, peak);
        Assert.InRange(maximum * gain, maximum, peak + 1e-10);
        Assert.InRange(gain, 1, 1.1);
    }

    [Fact]
    public void MatchingProxySamplingMakesZeroResidualAnIdentity()
    {
        // Simulate two successive resizing steps. Both branches must undergo each
        // step: one direct interpolation from the initial data is not equivalent.
        double[] source = [Proxy(0.01), Proxy(2), Proxy(0.2), Proxy(10)];
        var first = Resample(source, 8);
        var second = Resample(first, 5);
        var direct = Resample(source, 5);
        Assert.Contains(second.Zip(direct), pair => Math.Abs(pair.First - pair.Second) > 1e-6);
        foreach (var value in second)
            Assert.Equal(1, HdrGain(value, value, InverseProxy(value), 1, 49.3));
    }

    private static double Cas(double[] taps, double peak)
    {
        var center = taps[2];
        if (taps.Any(x => !double.IsFinite(x) || x < 0 || x > peak)) return center;
        var values = taps.Select(x => x / peak).ToArray();
        var low = values.Min();
        var high = values.Max();
        if (high - low < 1e-7) return center;
        var amplitude = Math.Sqrt(Math.Clamp(Math.Min(low, 1 - high) / Math.Max(high, 1e-8), 0, 1));
        var weight = -amplitude / 8;
        var filtered = Math.Clamp(((values.Sum() - values[2]) * weight + values[2]) / (1 + 4 * weight), 0, 1);
        return (values[2] * 0.65 + filtered * 0.35) * peak;
    }

    private static double Proxy(double radiance) => Math.Pow(radiance / (1 + radiance), 1 / 2.2);
    private static double InverseProxy(double value)
    {
        var power = Math.Pow(Math.Clamp(value, 0, 0.9999), 2.2);
        return power / Math.Max(1 - power, 1e-6);
    }

    private static double HdrGain(double processed, double baseline, double maximum, double gate, double peak)
    {
        if (maximum < 1e-5 || !double.IsFinite(processed)) return 1;
        var gain = Math.Clamp((InverseProxy(processed) + 1e-6) / (InverseProxy(baseline) + 1e-6), 0.9, 1.1);
        gain = 1 + (gain - 1) * Math.Clamp(gate, 0, 1);
        return Math.Min(gain, Math.Max(1, peak / maximum));
    }

    private static double[] Resample(double[] source, int size) => Enumerable.Range(0, size).Select(index =>
    {
        var position = (index + 0.5) * source.Length / size - 0.5;
        var left = (int)Math.Floor(position);
        var fraction = position - left;
        return source[Math.Clamp(left, 0, source.Length - 1)] * (1 - fraction)
            + source[Math.Clamp(left + 1, 0, source.Length - 1)] * fraction;
    }).ToArray();

    private static string FindShaderRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "third_party", "shaders");
            if (File.Exists(Path.Combine(candidate, "shaders.lock.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("无法找到着色器源码目录。");
    }

    private static void VerifyHashes(string root, JsonElement files)
    {
        foreach (var file in files.EnumerateArray())
        {
            var path = Path.Combine(root, file.GetProperty("path").GetString()!);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(file.GetProperty("sha256").GetString(), actual);
        }
    }
}
