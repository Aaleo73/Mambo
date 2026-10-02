using Mambo.Player.LibMpv;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class MpvNativeTests
{
    private static bool HasLibrary => File.Exists(Path.Combine(AppContext.BaseDirectory, "mpv", "libmpv-2.dll"));

    [Fact]
    public void RuntimeProbeSupportsComposition()
    {
        Assert.SkipUnless(HasLibrary, "没有 libmpv DLL，跳过真实组件验证。");
        var result = MpvRuntime.Probe();
        Assert.True(result.Available, result.Message);
        Assert.True(result.ApiVersion >= 0x00020005);
    }

    [Fact]
    public async Task HeadlessLoadReturnsEntryIdAndCopiesEvents()
    {
        Assert.SkipUnless(HasLibrary, "没有 libmpv DLL，跳过真实组件验证。");
        await using var player = new MpvCore(1280, 720, headless: true);
        var entry = await player.LoadFileAsync("av://lavfi:testsrc=size=128x72:rate=24", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(entry >= 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loaded = false;
        var started = false;
        await foreach (var message in player.Messages.ReadAllAsync(timeout.Token))
        {
            if (message is MpvMessage.StartFile start) { Assert.Equal(entry, start.EntryId); started = true; }
            if (message is MpvMessage.FileLoaded) loaded = true;
            if (message is MpvMessage.PlaybackRestart) break;
            Assert.IsNotType<MpvMessage.Failure>(message);
        }
        Assert.True(loaded);
        Assert.True(started);
        Assert.IsType<MpvValue.Map>(player.GetProperty("track-list") is MpvValue.Array array ? array.Values[0] : null);
        player.SetProperty("pause", true);
        await player.CommandAsync("seek", "0", "absolute");
    }

    [Fact]
    public async Task CreateDestroyTwentyTimesAndRejectCallsAfterDispose()
    {
        Assert.SkipUnless(HasLibrary, "没有 libmpv DLL，跳过真实组件验证。");
        await using (var warm = new MpvCore(128, 72, headless: true)) { }
        var before = Mambo.App.Debug.HandleDiagnostics.Capture();
        for (var i = 0; i < 20; i++)
        {
            var player = new MpvCore(128, 72, headless: true);
            await player.DisposeAsync();
            await player.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => player.SetProperty("pause", true));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = Mambo.App.Debug.HandleDiagnostics.Capture();
        var path = Environment.GetEnvironmentVariable("MAMBO_NATIVE_LIFECYCLE_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllLinesAsync(path, after.Select(pair => $"{pair.Key},{before.GetValueOrDefault(pair.Key)},{pair.Value}"),
                TestContext.Current.CancellationToken);
    }
}
