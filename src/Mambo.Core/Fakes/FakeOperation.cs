using Mambo.Core.Contracts;

namespace Mambo.Core.Fakes;

/// <summary>统一模拟延迟和错误；相同种子及调用顺序得到相同结果。</summary>
public sealed class FakeOperation
{
    private readonly FakeOptions options;
    private readonly TimeProvider clock;
    private readonly Random random;
    private readonly object gate = new();

    public FakeOperation(FakeOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Delay < TimeSpan.Zero || options.FailureRate is < 0 or > 1 || double.IsNaN(options.FailureRate))
            throw new ArgumentOutOfRangeException(nameof(options), "演示延迟或错误率无效。");
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
        random = new Random(options.Seed);
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(options.Delay, clock, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool fail;
        lock (gate) fail = random.NextDouble() < options.FailureRate;
        if (fail) throw new AppException(new AppError(AppErrorKind.Network, "demo.unavailable", "演示服务暂时不可用，请重试。", true));
    }
}
