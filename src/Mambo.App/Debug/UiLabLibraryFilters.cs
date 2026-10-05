using System.Collections.Immutable;
using System.Text.Json;
using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.App.Views;
using Mambo.Core.Contracts;
using Mambo.Core.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Mambo.App.Debug;

internal static partial class UiLabSmoke
{
    private static async Task RunLibraryFiltersOnlyAsync(MainWindow window, string reportPath, CancellationToken token)
    {
        var report = new MotionReport();
        try
        {
            await WaitAsync(() => window.Shell.IsLoaded && window.Shell.ActualWidth > 0, token);
            await RunLibraryFiltersAsync(window, report, reportPath, token);
            report.Passed = true;
            report.Stage = "Completed";
        }
        catch (Exception error) { report.FailureKind = error.GetType().Name; }
        finally
        {
            var path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, UiLabJsonContext.Default.MotionReport), CancellationToken.None);
            await window.CloseForSmokeAsync();
            window.Close();
        }
    }

    private static async Task RunLibraryFiltersAsync(MainWindow window, MotionReport report, string reportPath, CancellationToken token)
    {
        var services = window.Services;
        services.GetRequiredService<Navigator>().Navigate(Route.Settings);
        await window.Shell.PageHost.PendingTransition.WaitAsync(token);
        var fallback = services.GetRequiredService<ILibraryService>();
        var scheduler = services.GetRequiredService<IUiScheduler>();
        using var libraries = fallback.ObserveLibraries(token);
        await libraries.RefreshAsync(token);
        var movies = libraries.Current.First(item => item.Kind == LibraryKind.Movies);
        using var preferences = new FakeLibraryPreferences(scheduler);
        await preferences.SetAsync(movies.Id, new() { Genres = ["drama"] }, token);
        using var filters = new PendingFilterOptions();
        using var fixture = new MotionLibraryFixture(fallback, scheduler) { FilterOptionsOverride = filters };
        fixture.HoldLibrary(movies.Id);
        using var page = new LibraryPage(new LibraryViewModel(fixture, preferences, movies), services.GetRequiredService<WindowContext>());
        var mount = new Grid { Background = new SolidColorBrush(window.Shell.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 24, 26, 31) : Windows.UI.Color.FromArgb(255, 246, 247, 249)) };
        mount.Children.Add(page);
        Canvas.SetZIndex(mount, 1000);
        var well = window.Shell.FindName("WellContent").As<Grid>();
        well.Children.Add(mount);
        try
        {
            var before = new MediaItem("filter-before", "筛选前的内容", MediaKind.Movie)
            { Genres = ["Drama"], ProductionYear = 1992, OfficialRating = "PG" };
            fixture.LibraryRequests[^1].Query.Complete([before]);
            var viewModel = page.ViewModel;
            await MotionUntilAsync(() => viewModel.Cards.IsInitialized && page.IsLoaded, token);
            MotionCheck(report, "LoadedCardsSupplyFiltersWhileOptionsArePending", viewModel.IsLoadingFilters &&
                viewModel.DecadeChips.Any(chip => chip.Value == "1990") && viewModel.RatingChips.Any(chip => chip.Value == "PG"));
            var genre = viewModel.GenreChips.First(chip => chip.Value.Equals("drama", StringComparison.OrdinalIgnoreCase));
            MotionCheck(report, "RememberedFilterIsVisibleAndCaseInsensitive", genre.IsChecked && viewModel.FilterCount == 1);
            filters.Publish(new() { Genres = ["Drama", "Comedy"], Years = [1992], OfficialRatings = ["PG"] });
            MotionCheck(report, "ProgressiveOptionsPreserveChipInstances", ReferenceEquals(genre,
                viewModel.GenreChips.First(chip => chip.Value.Equals("drama", StringComparison.OrdinalIgnoreCase))));
            viewModel.FiltersOpen = true;
            await page.PendingFilterTransition.WaitAsync(token);
            await AwaitNextRenderingAsync(token);

            var oldCards = viewModel.Cards;
            var count = fixture.LibraryRequests.Count;
            var decade = viewModel.DecadeChips.First(chip => chip.Value == "1990");
            viewModel.Toggle(decade);
            viewModel.Toggle(genre);
            viewModel.Toggle(viewModel.GenreChips.First(chip => chip.Value == "Comedy"));
            MotionCheck(report, "RapidClicksWaitForOneQuery", fixture.LibraryRequests.Count == count && viewModel.IsApplying);
            await MotionUntilAsync(() => fixture.LibraryRequests.Count > count, token);
            var cancelled = fixture.LibraryRequests[^1];
            MotionCheck(report, "RapidClicksProduceOnlyTheFinalCombination", fixture.LibraryRequests.Count == count + 1 &&
                cancelled.Specification.Genres.SequenceEqual(["Comedy"]) && cancelled.Specification.Years.Length == 10);
            MotionCheck(report, "DecadeIncludesYearsNotYetLoaded", cancelled.Specification.Years.Contains(1990) && cancelled.Specification.Years.Contains(1999));
            MotionCheck(report, "PendingFilterKeepsResultsAndShowsStatus", ReferenceEquals(oldCards, viewModel.Cards) &&
                page.FindName("ApplyingState").As<FrameworkElement>().Visibility == Visibility.Visible);
            var shotRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "screenshots");
            await ShotAsync(window, shotRoot, "filter-pending", new UiLabReport(), token);
            InvokeMotionButton(page.FindName("CancelApplyButton").As<Button>());
            await MotionUntilAsync(() => !viewModel.IsApplying, token);
            cancelled.Query.Complete([before with { Id = "late", Name = "迟到的结果" }]);
            await AwaitNextRenderingAsync(token);
            MotionCheck(report, "CancelRestoresSelectionAndRejectsLateResults", cancelled.Query.IsDisposed &&
                ReferenceEquals(oldCards, viewModel.Cards) && viewModel.FilterCount == 1 && genre.IsChecked && !decade.IsChecked);

            count = fixture.LibraryRequests.Count;
            viewModel.Toggle(decade);
            await MotionUntilAsync(() => fixture.LibraryRequests.Count > count, token);
            fixture.LibraryRequests[^1].Query.Fail(new(AppErrorKind.Network, "filter.fixture", "筛选暂时无法加载，请重试。", true));
            await MotionUntilAsync(() => viewModel.HasApplyError, token);
            MotionCheck(report, "FailedFilterPreservesUsableResults", ReferenceEquals(oldCards, viewModel.Cards) && !viewModel.IsApplying &&
                page.FindName("ApplyErrorState").As<FrameworkElement>().Visibility == Visibility.Visible);
            await ShotAsync(window, shotRoot, "filter-retry", new UiLabReport(), token);
            count = fixture.LibraryRequests.Count;
            InvokeMotionButton(page.FindName("RetryApplyButton").As<Button>());
            await MotionUntilAsync(() => fixture.LibraryRequests.Count > count, token);
            fixture.LibraryRequests[^1].Query.Complete([before with { Id = "filter-after", Name = "筛选后的内容", ProductionYear = 1999 }]);
            await MotionUntilAsync(() => viewModel.Cards.Items[0].Id == "filter-after" && !viewModel.IsApplying, token);
            MotionCheck(report, "RetryCommitsLatestResultsAndClearsError", !viewModel.HasApplyError && viewModel.CountText == "1 项");

            count = fixture.LibraryRequests.Count;
            viewModel.Toggle(viewModel.GenreChips.First(chip => chip.Value.Length == 0));
            viewModel.Toggle(viewModel.DecadeChips.First(chip => chip.Value.Length == 0));
            viewModel.Toggle(viewModel.RatingChips.First(chip => chip.Value.Length == 0));
            await MotionUntilAsync(() => fixture.LibraryRequests.Count > count, token);
            var reset = fixture.LibraryRequests[^1];
            MotionCheck(report, "AllChipsClearTheirGroups", viewModel.FilterCount == 0 && reset.Specification.Genres.IsEmpty &&
                reset.Specification.Years.IsEmpty && reset.Specification.OfficialRatings.IsEmpty);
            reset.Query.Complete([before]);
            await MotionUntilAsync(() => !viewModel.IsApplying, token);
            count = fixture.LibraryRequests.Count;
            viewModel.ClearGroup(LibraryViewModel.GenreGroup);
            MotionCheck(report, "ClearingAnEmptyGroupDoesNotReload", fixture.LibraryRequests.Count == count);
            filters.Publish(new() { Genres = ["Drama", "Comedy"], Years = [1992, 1999], OfficialRatings = ["PG"] }, complete: true);
            MotionCheck(report, "CompletedOptionsClearLoadingStatus", !viewModel.IsLoadingFilters);
            await ShotAsync(window, shotRoot, "filter-ready", new UiLabReport(), token);

            count = fixture.LibraryRequests.Count;
            viewModel.Toggle(viewModel.DecadeChips.First(chip => chip.Value == "1990"));
            page.Dispose();
            await Task.Delay(250, token);
            MotionCheck(report, "LeavingCancelsDebouncedQueries", fixture.LibraryRequests.Count == count && filters.IsDisposed);
            await preferences.SetAsync(movies.Id, new() { Years = [1992] }, token);
            using var restoredFilters = new PendingFilterOptions();
            fixture.FilterOptionsOverride = restoredFilters;
            using var restored = new LibraryViewModel(fixture, preferences, movies);
            MotionCheck(report, "RememberedPartialDecadeIsExpanded", fixture.LibraryRequests[^1].Specification.Years.SequenceEqual(Enumerable.Range(1990, 10)) &&
                restored.DecadeChips.Single(chip => chip.Value == "1990").IsChecked);
        }
        finally { well.Children.Remove(mount); }
    }

    private sealed class PendingFilterOptions : IQuery<FilterOptions>
    {
        public FilterOptions? Current { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsRefreshing { get; private set; } = true;
        public AppError? Error => null;
        public bool IsDisposed { get; private set; }
        public event EventHandler? Updated;
        public void Publish(FilterOptions options, bool complete = false)
        {
            Current = options; IsInitialized = complete; IsRefreshing = !complete;
            Updated?.Invoke(this, EventArgs.Empty);
        }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { IsDisposed = true; Updated = null; }
    }
}
