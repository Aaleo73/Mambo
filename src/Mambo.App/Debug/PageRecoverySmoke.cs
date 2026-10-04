using System.Diagnostics;
using Mambo.App.Shell;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mambo.App.Debug;

/// <summary>
/// 独立挂载的真实 PageHost/ErrorPage 故障回归，不触及全局导航。
/// 异常的合成私密文本只存在于内存；报告只有固定检查名与布尔结果。
/// </summary>
internal static partial class PageRecoverySmoke
{
    public static async Task<PageRecoveryReport> RunAsync(MainWindow window, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(window);
        var report = new PageRecoveryReport();
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            Check(report, "UiThreadRequired", false);
            return report;
        }
        if (window.Services.GetRequiredService<ILibraryService>() is not FakeLibraryService ||
            window.Services.GetRequiredService<IPlaybackService>() is not FakePlaybackService ||
            window.Services.GetRequiredService<ISessionService>() is not FakeSessionService ||
            window.Services.GetRequiredService<ISettingsService>() is not FakeSettingsService ||
            window.Services.GetRequiredService<ILibraryPreferences>() is not FakeLibraryPreferences ||
            window.Services.GetRequiredService<IImageService>() is not FakeImageService)
        {
            Check(report, "FakeBackendRequired", false);
            return report;
        }
        if (!window.Shell.IsLoaded || window.Shell.ActivePlayer is not null ||
            window.Shell.FindName("Root") is not Grid root || root.XamlRoot is null)
        {
            Check(report, "BrowsingShellRootRequired", false);
            return report;
        }
        var globalNavigation = window.Services.GetRequiredService<Navigator>();
        var playback = window.Services.GetRequiredService<IPlaybackService>();
        if (globalNavigation.ForwardBlocked || playback.IsStarting || playback.Current is not null)
        {
            Check(report, "IdleBrowsingRequired", false);
            return report;
        }
        var globalEntry = globalNavigation.Current;
        var globalPage = window.Shell.PageHost.CurrentPage;
        var previousFocus = FocusManager.GetFocusedElement(root.XamlRoot) as Control;
        try
        {
            foreach (var kind in new[] { FaultKind.Factory, FaultKind.Enter, FaultKind.Leave })
            {
                token.ThrowIfCancellationRequested();
                await RunCaseAsync(root, window.Services.GetRequiredService<WindowContext>(), kind, report, token);
            }
        }
        catch (OperationCanceledException) { Check(report, "ProbeCancelled", false); }
        catch (Exception) { Check(report, "UnexpectedProbeFailure", false); }
        finally
        {
            Check(report, "GlobalNavigationUnchanged", ReferenceEquals(globalEntry, globalNavigation.Current) &&
                ReferenceEquals(globalPage, window.Shell.PageHost.CurrentPage));
            try { if (previousFocus is { IsLoaded: true, IsEnabled: true }) previousFocus.Focus(FocusState.Programmatic); }
            catch (Exception) { Check(report, "OriginalFocusRestoreFailed", false); }
        }
        report.CleanupPassed = report.Checks.Count(check => check.Name.EndsWith("OwnedCleanup", StringComparison.Ordinal)) == 3 &&
            report.Checks.Where(check => check.Name.EndsWith("OwnedCleanup", StringComparison.Ordinal)).All(check => check.Passed);
        report.Passed = report.CleanupPassed && report.Checks.Count > 0 && report.Checks.All(check => check.Passed);
        return report;
    }

    private static async Task RunCaseAsync(Grid root, WindowContext window, FaultKind kind, PageRecoveryReport report, CancellationToken token)
    {
        var prefix = kind switch { FaultKind.Factory => "FactoryThrow", FaultKind.Enter => "NavigatedToThrow", _ => "NavigatedFromThrow" };
        var navigation = new Navigator();
        var fixture = new Fixture(navigation, kind);
        using var transitions = new BrowseTransitionCoordinator(navigation, window);
        using var host = new PageHost { Background = root.Background, Margin = new Thickness(20) };
        Grid.SetRow(host, 1);
        Grid.SetColumn(host, 1);
        NavigatedEventArgs? last = null;
        var eventCount = 0;
        void OnNavigated(object? sender, NavigatedEventArgs args)
        {
            last = args;
            eventCount++;
            host.Show(args);
        }
        // 没有异常日志接收方：带合成私密文本的故障不能进入应用日志。
        host.Initialize(fixture.Create, navigation, transitions);
        navigation.Navigated += OnNavigated;
        var errors = new List<ErrorPage>();
        var completed = false;
        try
        {
            root.Children.Add(host);
            host.Show(new NavigatedEventArgs(null, navigation.Current, NavigationMode.New));
            await HealthyAsync(host, token);
            var homeEntry = navigation.Current;
            navigation.Navigate(Route.Recent);
            await HealthyAsync(host, token);
            var anchor = navigation.Current;
            var firstOldPages = fixture.Pages.ToArray();

            // 第一次真实失败验证旧页与失败工厂/候选页的作用域清理。
            fixture.Arm();
            navigation.Navigate(fixture.Target);
            var failedEntry = navigation.Current;
            var firstError = await ErrorAsync(host, token);
            errors.Add(firstError);
            Assert(report, prefix + "InitialFixedErrorText", FixedErrorText(firstError, fixture.PrivateMessage));
            Assert(report, prefix + "InitialScopesReleased", firstOldPages.All(page => page.Scope.ReleasedExactlyOnce) &&
                fixture.LastFailedPage?.Scope.ReleasedExactlyOnce == true);
            Assert(report, prefix + "FailedEntryStateCleared", failedEntry.ViewState is null && failedEntry.VerticalOffset == 0);

            // 建立真实前进记录，再通过后退触发同条失败记录。重试必须保留两向历史。
            navigation.Navigate(Route.Settings);
            var leaving = await HealthyAsync(host, token);
            var forwardEntry = navigation.Current;
            forwardEntry.VerticalOffset = 33;
            forwardEntry.ViewState = new object();
            fixture.Arm();
            Assert(report, prefix + "FailureBackUsesSameEntry", navigation.GoBack() && ReferenceEquals(navigation.Current, failedEntry));
            var secondError = await ErrorAsync(host, token);
            errors.Add(secondError);
            Assert(report, prefix + "SecondFixedErrorText", FixedErrorText(secondError, fixture.PrivateMessage));
            Assert(report, prefix + "FailureClearsLeavingScope", leaving.Scope.ReleasedExactlyOnce && fixture.LastFailedPage?.Scope.ReleasedExactlyOnce == true);
            if (kind == FaultKind.Leave)
                Assert(report, prefix + "LeavingEntryStateCleared", forwardEntry.ViewState is null && forwardEntry.VerticalOffset == 0);
            Assert(report, prefix + "BothHistoryDirectionsBeforeRetry", navigation.CanGoBack && navigation.CanGoForward);

            var targetFactoryCalls = fixture.TargetFactoryCalls;
            var eventsBefore = eventCount;
            await InvokeAsync(secondError, "RetryButton", "重试页面", token);
            var recovered = await HealthyAsync(host, token);
            Assert(report, prefix + "RetrySameEntrySingleReplace", eventCount == eventsBefore + 1 &&
                last?.Mode == NavigationMode.Replace && ReferenceEquals(last.From, failedEntry) &&
                ReferenceEquals(last.To, failedEntry) && ReferenceEquals(navigation.Current, failedEntry));
            Assert(report, prefix + "RetryConstructsHealthyPage", fixture.TargetFactoryCalls == targetFactoryCalls + 1 &&
                !recovered.Scope.IsDisposed && recovered.Scope.CancelCount == 0);
            Assert(report, prefix + "BothHistoryDirectionsAfterRetry", navigation.CanGoBack && navigation.CanGoForward);
            await WaitAsync(() => errors.All(error => !host.Children.Contains(error) && !error.IsLoaded), token);
            Assert(report, prefix + "ErrorsRemovedAfterRetry", !host.Children.OfType<ErrorPage>().Any());

            // 实际走完整历史链，不能只凭 CanGoBack/CanGoForward 推断记录未污染。
            await TraverseAsync(host, navigation, forward: true, forwardEntry, report, prefix + "ForwardAfterRetry", token);
            Assert(report, prefix + "ForwardBoundaryUnchanged", !navigation.GoForward());
            await TraverseAsync(host, navigation, forward: false, failedEntry, report, prefix + "BackToRecoveredEntry", token);
            await TraverseAsync(host, navigation, forward: false, anchor, report, prefix + "BackToAnchor", token);
            await TraverseAsync(host, navigation, forward: false, homeEntry, report, prefix + "BackToHome", token);
            Assert(report, prefix + "BackBoundaryUnchanged", !navigation.GoBack());
            await TraverseAsync(host, navigation, forward: true, anchor, report, prefix + "ForwardToAnchor", token);
            await TraverseAsync(host, navigation, forward: true, failedEntry, report, prefix + "ForwardToRecoveredEntry", token);
            await TraverseAsync(host, navigation, forward: true, forwardEntry, report, prefix + "ForwardToOriginalEnd", token);
            Assert(report, prefix + "ForwardEndUnchanged", !navigation.GoForward());

            // 淘汰目标页，再制造故障，验证真实“返回首页”按钮以及错误页不进缓存。
            navigation.Navigate(Route.Library("probe-library-a"));
            await HealthyAsync(host, token);
            navigation.Navigate(Route.Library("probe-library-b"));
            await HealthyAsync(host, token);
            Assert(report, prefix + "TargetEvictedForHomeAction", !host.Children.Contains(recovered));
            fixture.Arm();
            navigation.Navigate(fixture.Target);
            var homeFailureEntry = navigation.Current;
            var homeError = await ErrorAsync(host, token);
            errors.Add(homeError);
            Assert(report, prefix + "HomeActionFixedErrorText", FixedErrorText(homeError, fixture.PrivateMessage));
            await InvokeAsync(homeError, "HomeButton", "返回首页", token);
            await HealthyAsync(host, token);
            var recoveredHomeEntry = navigation.Current;
            Assert(report, prefix + "HomeButtonNavigatesHome", navigation.Current.Route.Equals(Route.Home) && last?.Mode == NavigationMode.New);
            await TraverseAsync(host, navigation, forward: false, homeFailureEntry, report, prefix + "BackRecreatesFailedRoute", token);
            Assert(report, prefix + "ErrorPageNotCached", !host.Children.OfType<ErrorPage>().Any() && host.CurrentPage is ProbePage);
            await TraverseAsync(host, navigation, forward: true, recoveredHomeEntry, report, prefix + "ForwardReturnsHome", token);
            completed = true;
        }
        catch (OperationCanceledException) { Check(report, prefix + "Cancelled", false); }
        catch (CheckFailure error)
        {
            if (!report.Checks.Any(check => check.Name == error.Check && !check.Passed)) Check(report, prefix + error.Check, false);
        }
        catch (Exception) { Check(report, prefix + "UnexpectedFailure", false); }
        finally
        {
            var eventsBeforeDetach = eventCount;
            navigation.Navigated -= OnNavigated;
            navigation.Reset(Route.Home);
            var unsubscribed = eventCount == eventsBeforeDetach;
            var clearSucceeded = true;
            try { host.Clear(); }
            catch (Exception) { clearSucceeded = false; }
            var allReleasedByHost = fixture.Pages.All(page => page.Scope.ReleasedExactlyOnce);
            // 即使回归失败，也回收探针拥有的未释放作用域；不能把这次补救算作通过。
            foreach (var page in fixture.Pages.Where(page => !page.Scope.IsDisposed)) page.Dispose();
            var removed = false;
            try { root.Children.Remove(host); removed = !root.Children.Contains(host); }
            catch (Exception) { }
            try { await WaitAsync(() => !host.IsLoaded && errors.All(error => !error.IsLoaded) && fixture.Pages.All(page => !page.IsLoaded), CancellationToken.None); }
            catch (Exception) { removed = false; }
            Check(report, prefix + "OwnedCleanup", clearSucceeded && allReleasedByHost && removed && unsubscribed && host.Children.Count == 0);
        }
        Check(report, prefix + "Completed", completed);
    }

    private static async Task TraverseAsync(PageHost host, Navigator navigation, bool forward, NavEntry expected,
        PageRecoveryReport report, string name, CancellationToken token)
    {
        Assert(report, name, (forward ? navigation.GoForward() : navigation.GoBack()) && ReferenceEquals(navigation.Current, expected));
        await HealthyAsync(host, token);
    }

    private static async Task<ProbePage> HealthyAsync(PageHost host, CancellationToken token)
    {
        await WaitAsync(() => !host.IsTransitioning && host.CurrentPage is ProbePage page && Loaded(page) && !page.Scope.IsDisposed, token);
        return (ProbePage)host.CurrentPage!;
    }

    private static async Task<ErrorPage> ErrorAsync(PageHost host, CancellationToken token)
    {
        await WaitAsync(() => host.CurrentPage is ErrorPage page && Loaded(page), token);
        return (ErrorPage)host.CurrentPage!;
    }

    private static bool Loaded(FrameworkElement page) => page.IsLoaded && page.XamlRoot is not null && page.ActualWidth > 0 && page.ActualHeight > 0;

    private static async Task InvokeAsync(ErrorPage error, string buttonName, string accessibleName, CancellationToken token)
    {
        var button = error.FindName(buttonName) as Button ?? throw new CheckFailure("RecoveryButtonMissing");
        await WaitAsync(() => button.IsLoaded && button.IsEnabled && button.ActualWidth > 0, token);
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer is null || peer.GetName() != accessibleName || peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new CheckFailure("RecoveryButtonInvokePatternMissing");
        invoke.Invoke();
        await Task.Yield();
    }

    private static bool FixedErrorText(ErrorPage error, string privateMessage)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "出了点问题", "页面暂时无法显示，请重试或返回首页。", "返回首页", "重试" };
        // FontIcon 的内部 TextBlock 是装饰字形，不是错误文案。
        var text = Descendants(error).OfType<TextBlock>().Where(block => !HasIconAncestor(block, error))
            .Select(block => block.Text).Where(value => !string.IsNullOrEmpty(value)).ToArray();
        var names = Descendants(error).OfType<Button>().Select(AutomationProperties.GetName).ToArray();
        return AutomationProperties.GetName(error) == "页面错误" && text.Contains("出了点问题", StringComparer.Ordinal) &&
            text.Contains("页面暂时无法显示，请重试或返回首页。", StringComparer.Ordinal) &&
            text.All(value => allowed.Contains(value) && !value.Contains(privateMessage, StringComparison.Ordinal) &&
                !value.Contains("fixture.invalid", StringComparison.Ordinal) && !value.Contains("password", StringComparison.Ordinal)) &&
            names.Length == 2 && names.Contains("返回首页", StringComparer.Ordinal) && names.Contains("重试页面", StringComparer.Ordinal);
    }

    private static bool HasIconAncestor(DependencyObject node, DependencyObject root)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null && !ReferenceEquals(parent, root); parent = VisualTreeHelper.GetParent(parent))
            if (parent is IconElement) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= TimeSpan.FromSeconds(5)) throw new CheckFailure("RecoveryLayoutTimedOut");
            await Task.Delay(20, token);
        }
    }

    private static void Check(PageRecoveryReport report, string name, bool passed) => report.Checks.Add(new() { Name = name, Passed = passed });
    private static void Assert(PageRecoveryReport report, string name, bool passed)
    {
        Check(report, name, passed);
        if (!passed) throw new CheckFailure(name);
    }

    private enum FaultKind { Factory, Enter, Leave }
    private sealed class CheckFailure(string check) : Exception("PageRecoveryCheckFailed")
    {
        public string Check { get; } = check;
    }

    private sealed class Fixture(Navigator navigation, FaultKind kind)
    {
        private bool armed;
        public Route Target { get; } = Route.Detail("probe-target");
        public string PrivateMessage { get; } = "https://fixture.invalid/" + Guid.NewGuid().ToString("N") + "?password=" + Guid.NewGuid().ToString("N");
        public List<ProbePage> Pages { get; } = [];
        public ProbePage? LastFailedPage { get; private set; }
        public int TargetFactoryCalls { get; private set; }
        public void Arm() => armed = true;

        public ProbePage Create(Route route)
        {
            var page = new ProbePage(this);
            Pages.Add(page);
            if (route.Equals(Target)) TargetFactoryCalls++;
            try
            {
                if (route.Equals(Target)) Fail(FaultKind.Factory, page);
                return page;
            }
            catch
            {
                // 工厂返回前的作用域仍由工厂拥有，与生产 PageFactory.CreateOwned 同责。
                page.Dispose();
                throw;
            }
        }

        public void Enter(ProbePage page, NavEntry entry)
        {
            if (!entry.Route.Equals(Target)) return;
            entry.VerticalOffset = 77;
            entry.ViewState = new object();
            Fail(FaultKind.Enter, page);
        }

        public void Leave(ProbePage page)
        {
            if (navigation.Current.Route.Equals(Target)) Fail(FaultKind.Leave, page);
        }

        private void Fail(FaultKind at, ProbePage page)
        {
            if (!armed || at != kind) return;
            armed = false;
            LastFailedPage = page;
            throw new InvalidOperationException(PrivateMessage);
        }
    }

    private sealed partial class ProbePage(Fixture fixture) : UserControl, INavigablePage, IDisposable
    {
        public ProbeScope Scope { get; } = new();
        public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
        {
            Content ??= new TextBlock { Text = "页面恢复验证", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            fixture.Enter(this, entry);
        }
        public void OnNavigatedFrom(NavEntry entry) => fixture.Leave(this);
        public void Refresh() { }
        public void Dispose() => Scope.Dispose();
    }

    private sealed class ProbeScope : IDisposable
    {
        private readonly CancellationTokenSource source = new();
        private readonly CancellationTokenRegistration cancellation;
        public ProbeScope() => cancellation = source.Token.Register(() => CancelCount++);
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool ReleasedExactlyOnce => IsDisposed && DisposeCount == 1 && CancelCount == 1;
        public void Dispose()
        {
            DisposeCount++;
            if (IsDisposed) return;
            IsDisposed = true;
            source.Cancel();
            cancellation.Dispose();
            source.Dispose();
        }
    }
}

internal sealed class PageRecoveryReport
{
    public bool Passed { get; set; }
    public bool CleanupPassed { get; set; }
    public List<PageRecoveryCheck> Checks { get; set; } = [];
}

internal sealed class PageRecoveryCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
}
