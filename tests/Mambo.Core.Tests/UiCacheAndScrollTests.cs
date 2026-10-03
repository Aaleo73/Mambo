using Mambo.App.Images;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class UiCacheAndScrollTests
{
    [Fact]
    public void ClearingImageCacheCancelsOldAccountAndRejectsItsLateDecode()
    {
        using var cache = new DecodedImageCache<object>();
        var image = new ImageRef("same-id", ImageKind.Primary);
        var oldGeneration = cache.Generation;
        var oldToken = cache.Token;
        var first = new object();
        Assert.True(cache.Set(image, 150, first, oldGeneration));
        Assert.Same(first, cache.Get(image, 150));
        cache.Clear();
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(cache.Token.IsCancellationRequested);
        Assert.Null(cache.Get(image, 150));
        Assert.False(cache.Set(image, 150, first, oldGeneration));
        var second = new object();
        Assert.True(cache.Set(image, 150, second, cache.Generation));
        Assert.Same(second, cache.Get(image, 150));
    }

    [Fact]
    public void WeakImageKeysStayBoundedAndRecentHitSurvivesEviction()
    {
        using var cache = new DecodedImageCache<object>(2);
        var first = new ImageRef("first", ImageKind.Primary);
        var second = new ImageRef("second", ImageKind.Primary);
        var third = new ImageRef("third", ImageKind.Primary);
        var held = new object[] { new(), new(), new() };
        cache.Set(first, 150, held[0], cache.Generation);
        cache.Set(second, 150, held[1], cache.Generation);
        Assert.Same(held[0], cache.Get(first, 150));
        cache.Set(third, 150, held[2], cache.Generation);
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get(second, 150));
        Assert.Same(held[0], cache.Get(first, 150));
        GC.KeepAlive(held);
    }

    [Fact]
    public async Task DeepOffsetLoadsEachPageUntilReachableWithoutStoppingAtFirstPage()
    {
        var available = 400d;
        var calls = 0;
        var positions = new List<double>();
        await PagedScrollRestorer.RestoreAsync(2500, () => available, positions.Add, token =>
        {
            token.ThrowIfCancellationRequested();
            available += 800;
            calls++;
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
        Assert.Equal(2500, positions[^1]);
        Assert.All(positions, value => Assert.InRange(value, 0, 2500));
    }

    [Fact]
    public async Task EndOrFailureClampsToAvailableContentWithoutRepeatedLoading()
    {
        var available = 400d;
        var calls = 0;
        var position = 0d;
        await PagedScrollRestorer.RestoreAsync(2500, () => available, value => position = value, _ =>
        {
            available = 1200;
            calls++;
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(1200, position);
    }

    [Fact]
    public async Task LeavingPageCancelsRestorationAndDoesNotStartAnotherPage()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PagedScrollRestorer.RestoreAsync(2500, () => 400, _ => { }, token =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }, cancellation.Token));
        Assert.Equal(1, calls);
    }
}
