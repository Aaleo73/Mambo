using System.Globalization;

namespace Mambo.Core.Fakes;

/// <summary>演示数据的确定性运行配置，不包含地址或认证信息。</summary>
public sealed record FakeOptions
{
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(120);
    public double FailureRate { get; init; }
    public int Seed { get; init; } = 20261002;
    public TimeSpan PlaybackTick { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan BufferEvery { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromSeconds(2);

    public static FakeOptions FromEnvironment(string? delayMilliseconds, string? failureRate)
    {
        var options = new FakeOptions();
        if (int.TryParse(delayMilliseconds, CultureInfo.InvariantCulture, out var delay))
            options = options with { Delay = TimeSpan.FromMilliseconds(Math.Clamp(delay, 0, 10000)) };
        if (double.TryParse(failureRate, CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate))
            options = options with { FailureRate = Math.Clamp(rate, 0, 1) };
        return options;
    }
}
