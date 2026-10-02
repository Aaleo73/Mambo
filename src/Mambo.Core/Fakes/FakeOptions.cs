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
}
