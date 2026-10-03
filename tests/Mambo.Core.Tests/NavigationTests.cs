using System.Globalization;
using Mambo.App.Shell;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void BackAndForwardKeepAtMostTwelveEntries()
    {
        var navigation = new Navigator();
        for (var index = 1; index <= 20; index++) navigation.Navigate(Route.Detail(index.ToString(CultureInfo.InvariantCulture)));
        for (var index = 19; index >= 8; index--)
        {
            Assert.True(navigation.GoBack());
            Assert.Equal(index.ToString(CultureInfo.InvariantCulture), navigation.Current.Route.Parameter);
        }
        Assert.False(navigation.GoBack());
        for (var index = 9; index <= 20; index++)
        {
            Assert.True(navigation.GoForward());
            Assert.Equal(index.ToString(CultureInfo.InvariantCulture), navigation.Current.Route.Parameter);
        }
        Assert.False(navigation.GoForward());
    }

    [Fact]
    public void ReplacingSearchKeepsOneHistoryEntryAndRestoresStoredScroll()
    {
        var navigation = new Navigator();
        navigation.Navigate(Route.Search("first"));
        navigation.Navigate(Route.Search("second"));
        var search = navigation.Current;
        search.VerticalOffset = 345;
        navigation.Navigate(Route.Detail("item"));
        Assert.True(navigation.GoBack());
        Assert.Same(search, navigation.Current);
        Assert.Equal(345, navigation.Current.VerticalOffset);
        Assert.True(navigation.GoBack());
        Assert.Equal(PageKind.Home, navigation.Current.Route.Kind);
        Assert.False(navigation.GoBack());
    }

    [Fact]
    public void PlaybackBlocksBackgroundNavigationAndBackClosesOverlay()
    {
        var navigation = new Navigator();
        navigation.Navigate(Route.Settings);
        navigation.GoBack();
        var current = navigation.Current;
        var interceptor = new BackHandler();
        navigation.BackInterceptor = interceptor;
        navigation.ForwardBlocked = true;
        Assert.False(navigation.GoForward());
        navigation.Navigate(Route.Recent);
        Assert.Same(current, navigation.Current);
        Assert.True(navigation.GoBack());
        Assert.Equal(1, interceptor.Count);
        Assert.Same(current, navigation.Current);
        navigation.BackInterceptor = null;
        navigation.ForwardBlocked = false;
        Assert.True(navigation.GoForward());
        Assert.Equal(PageKind.Settings, navigation.Current.Route.Kind);
    }

    [Fact]
    public void AccountResetDropsOldNavigationHistory()
    {
        var navigation = new Navigator();
        navigation.Navigate(Route.Recent);
        navigation.Navigate(Route.Settings);
        navigation.GoBack();
        navigation.Reset(Route.Home);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);
        Assert.Equal(PageKind.Home, navigation.Current.Route.Kind);
    }

    [Fact]
    public void RetryingFailedPageKeepsItsEntryAndBothHistoryDirections()
    {
        var navigation = new Navigator();
        navigation.Navigate(Route.Recent);
        navigation.Navigate(Route.Settings);
        Assert.True(navigation.GoBack());
        var failed = navigation.Current;
        failed.VerticalOffset = 345;
        var viewState = new object();
        failed.ViewState = viewState;
        NavigatedEventArgs? retry = null;
        var count = 0;
        navigation.Navigated += (_, args) => { retry = args; count++; };

        navigation.RetryCurrent();

        Assert.Equal(1, count);
        Assert.NotNull(retry);
        Assert.Equal(NavigationMode.Replace, retry.Mode);
        Assert.Same(failed, retry.From);
        Assert.Same(failed, retry.To);
        Assert.Same(failed, navigation.Current);
        Assert.Equal(345, failed.VerticalOffset);
        Assert.Same(viewState, failed.ViewState);
        Assert.True(navigation.CanGoBack);
        Assert.True(navigation.CanGoForward);
        Assert.True(navigation.GoForward());
        Assert.Equal(PageKind.Settings, navigation.Current.Route.Kind);
        Assert.True(navigation.GoBack());
        Assert.Same(failed, navigation.Current);
        Assert.True(navigation.GoBack());
        Assert.Equal(PageKind.Home, navigation.Current.Route.Kind);
    }

    [Fact]
    public void RetryingHomeRaisesNavigationAndPlaybackStillBlocksRetries()
    {
        var navigation = new Navigator();
        var original = navigation.Current;
        var count = 0;
        navigation.Navigated += (_, _) => count++;
        navigation.RetryCurrent();
        Assert.Equal(1, count);
        Assert.Same(original, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.CanGoForward);

        navigation.ForwardBlocked = true;
        navigation.RetryCurrent();
        Assert.Equal(1, count);
        Assert.Same(original, navigation.Current);
    }

    private sealed class BackHandler : IBackInterceptor
    {
        public int Count { get; private set; }
        public bool CanHandle => true;
        public bool TryHandleBack() { Count++; return true; }
    }
}
