using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;

namespace Mambo.App.ViewModels;

/// <summary>详情观察与选季、选集状态；所有查询只依赖前后端契约。</summary>
public sealed partial class DetailViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService library;
    private readonly PlaybackLauncher launcher;
    private readonly IPlaybackService playback;
    private readonly CancellationTokenSource scope = new();
    private readonly IQuery<MediaItem> detail = null!;
    private IQuery<MediaItem>? seriesDetail;
    private IQuery<MediaItem>? nextUp;
    private IQuery<ImmutableArray<SeasonInfo>>? seasons;
    private IPagedQuery<MediaItem>? episodes;
    private MediaItem? item;
    private MediaItem? selectedEpisode;
    private string? seriesId;
    private string? selectedSeasonId;
    private string? preferredEpisodeId;
    private string? preferredSeasonId;
    private string? notifiedEpisodeId;
    private bool manualSeason;
    private bool manualEpisode;
    private bool locatingEpisode;
    private bool disposed;
    private int episodeGeneration;

    public DetailViewModel(ILibraryService library, PlaybackLauncher launcher, IPlaybackService playback, string itemId)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        this.library = library;
        this.launcher = launcher;
        this.playback = playback;
        try
        {
            detail = library.ObserveDetail(itemId, scope.Token);
            detail.Updated += OnDetailUpdated;
            playback.Changed += OnStartingChanged;
            ApplyDetail();
        }
        catch
        {
            FailedConstruction.Release(scope.Cancel, () => playback.Changed -= OnStartingChanged,
                () => { if (detail is not null) detail.Updated -= OnDetailUpdated; },
                () => detail?.Dispose(),
                () => { if (seriesDetail is not null) seriesDetail.Updated -= OnSeriesUpdated; }, () => seriesDetail?.Dispose(),
                () => { if (nextUp is not null) nextUp.Updated -= OnSeriesUpdated; }, () => nextUp?.Dispose(),
                () => { if (seasons is not null) seasons.Updated -= OnSeriesUpdated; }, () => seasons?.Dispose(),
                () => { if (episodes is not null) episodes.Updated -= OnEpisodesUpdated; }, () => episodes?.Dispose(), scope.Dispose);
            throw;
        }
    }

    public ObservableCollection<DetailSeasonViewModel> Seasons { get; } = [];
    public ObservableCollection<DetailEpisodeViewModel> Episodes { get; } = [];
    public ObservableCollection<DetailPersonViewModel> People { get; } = [];
    public event EventHandler? TargetEpisodeAvailable;

    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial bool HasContent { get; private set; }
    [ObservableProperty] public partial bool HasRefreshError { get; private set; }
    [ObservableProperty] public partial string ErrorText { get; private set; } = "";
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Overview { get; private set; } = "";
    [ObservableProperty] public partial string RatingStar { get; private set; } = "";
    [ObservableProperty] public partial string Metadata { get; private set; } = "";
    [ObservableProperty] public partial string EpisodeLine { get; private set; } = "";
    [ObservableProperty] public partial string PlayLabel { get; private set; } = "播放";
    [ObservableProperty] public partial string PlayHint { get; private set; } = "";
    [ObservableProperty] public partial string PlayItemId { get; private set; } = "";
    [ObservableProperty] public partial object? Backdrop { get; private set; }
    [ObservableProperty] public partial object? Logo { get; private set; }
    [ObservableProperty] public partial bool ShowEpisodeSection { get; private set; }
    [ObservableProperty] public partial bool IsSeriesLoading { get; private set; }
    [ObservableProperty] public partial string SeriesErrorText { get; private set; } = "";
    [ObservableProperty] public partial bool HasSeriesError { get; private set; }
    [ObservableProperty] public partial bool IsEpisodesLoading { get; private set; }
    [ObservableProperty] public partial bool IsLoadingMore { get; private set; }
    [ObservableProperty] public partial bool HasEpisodesError { get; private set; }
    [ObservableProperty] public partial bool HasEpisodeMoreError { get; private set; }
    [ObservableProperty] public partial bool IsEpisodesEmpty { get; private set; }
    [ObservableProperty] public partial string EpisodeErrorText { get; private set; } = "";
    [ObservableProperty] public partial double ProgressFraction { get; private set; }
    [ObservableProperty] public partial bool IsStarting { get; private set; }

    public bool HasLogo => Logo is not null;
    public bool HasTitle => Logo is null;
    public bool HasEpisodeLine => EpisodeLine.Length > 0;
    public bool HasPeople => People.Count > 0;
    public bool HasProgress => ProgressFraction > 0;
    public bool CanPlay => !IsStarting && PlayItemId.Length > 0;
    public bool CanReplay => CanPlay && ProgressFraction > 0;
    public string? SelectedEpisodeId => selectedEpisode?.Id;
    public string? SelectedSeasonId => selectedSeasonId;
    public bool HasMoreEpisodes => episodes?.HasMore ?? false;

    public async Task PlayAsync(bool fromBeginning = false)
    {
        if (!CanPlay) return;
        await launcher.PlayAsync(PlayItemId, fromBeginning ? 0 : null);
    }

    public Task PlayEpisodeAsync(string itemId) => launcher.PlayAsync(itemId);

    public void SelectEpisode(string itemId)
    {
        var target = Episodes.FirstOrDefault(e => e.Id == itemId)?.Item;
        if (target is null) return;
        manualEpisode = true;
        preferredEpisodeId = target.Id;
        selectedEpisode = target;
        ApplyHero();
        ApplyEpisodeSelection();
    }

    public void SelectSeason(string seasonId)
    {
        if (!Seasons.Any(s => s.Id == seasonId)) return;
        manualSeason = true;
        if (selectedSeasonId == seasonId) return;
        manualEpisode = false;
        preferredEpisodeId = null;
        selectedEpisode = null;
        ChangeSeason(seasonId);
    }

    public void RestoreSelection(string? seasonId, string? episodeId)
    {
        preferredSeasonId = seasonId;
        preferredEpisodeId = episodeId;
        manualSeason = seasonId is not null;
        manualEpisode = episodeId is not null;
        ApplySeries();
    }

    public async Task RefreshAsync()
    {
        var work = new List<Task> { detail.RefreshAsync(scope.Token) };
        if (seriesDetail is not null) work.Add(seriesDetail.RefreshAsync(scope.Token));
        if (nextUp is not null) work.Add(nextUp.RefreshAsync(scope.Token));
        if (seasons is not null) work.Add(seasons.RefreshAsync(scope.Token));
        if (episodes is not null) work.Add(episodes.RefreshAsync(scope.Token));
        try { await Task.WhenAll(work); }
        catch (OperationCanceledException) { }
    }

    public async Task LoadMoreAsync()
    {
        var query = episodes;
        if (query is null || query.IsLoading || !query.HasMore) return;
        try { await query.LoadMoreAsync(scope.Token); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        scope.Cancel();
        playback.Changed -= OnStartingChanged;
        detail.Updated -= OnDetailUpdated;
        detail.Dispose();
        DisposeSeries();
        scope.Dispose();
    }

    private void OnDetailUpdated(object? sender, EventArgs e) => ApplyDetail();
    private void OnSeriesUpdated(object? sender, EventArgs e) { ApplySeries(); ApplyHero(); }
    private void OnEpisodesUpdated(object? sender, EventArgs e) => ApplyEpisodes();
    private void OnStartingChanged(object? sender, EventArgs e)
    {
        IsStarting = playback.IsStarting;
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanReplay));
    }

    private void ApplyDetail()
    {
        if (disposed) return;
        IsLoading = !detail.IsInitialized && detail.Error is null;
        HasError = detail.Error is not null && detail.Current is null;
        HasRefreshError = detail.Error is not null && detail.IsInitialized && detail.Current is not null;
        ErrorText = detail.Error?.Message ?? "";
        item = detail.IsInitialized ? detail.Current : null;
        if (item is null) { HasContent = false; return; }
        var wanted = item.Kind == MediaKind.Series ? item.Id : item.SeriesId;
        if (seriesId != wanted)
        {
            DisposeSeries();
            seriesId = wanted;
            if (wanted is not null)
            {
                if (item.Kind != MediaKind.Series)
                {
                    seriesDetail = library.ObserveDetail(wanted, scope.Token);
                    seriesDetail.Updated += OnSeriesUpdated;
                }
                nextUp = library.ObserveNextUp(wanted, scope.Token);
                seasons = library.ObserveSeasons(wanted, scope.Token);
                nextUp.Updated += OnSeriesUpdated;
                seasons.Updated += OnSeriesUpdated;
                if (item.Kind == MediaKind.Episode) preferredEpisodeId = item.Id;
            }
        }
        if (selectedEpisode?.Id == item.Id) selectedEpisode = item;
        ApplySeries();
        ApplyHero();
        HasContent = true;
        OnStartingChanged(null, EventArgs.Empty);
    }

    private void ApplySeries()
    {
        if (disposed) return;
        ShowEpisodeSection = seriesId is not null;
        IsSeriesLoading = seasons is { IsInitialized: false, Error: null } || nextUp is { IsInitialized: false, Error: null };
        SeriesErrorText = nextUp?.Error is { } upError ? "无法确定接下来播放的剧集：" + upError.Message
            : seasons?.Error is { } seasonError ? "无法加载剧集季：" + seasonError.Message
            : seriesDetail?.Error is { } seriesError ? "剧集信息加载失败：" + seriesError.Message : "";
        HasSeriesError = SeriesErrorText.Length > 0;
        var values = seasons.ItemsOrEmpty();
        if (!values.Select(s => s.Id).SequenceEqual(Seasons.Select(s => s.Id)) ||
            values.Where((s, index) => s != Seasons[index].Season).Any())
        {
            Seasons.Clear();
            foreach (var season in values) Seasons.Add(new DetailSeasonViewModel(season));
        }
        var target = item?.Kind == MediaKind.Episode && !manualEpisode ? item : nextUp?.Current;
        if (!manualEpisode && target is not null && !manualSeason)
        {
            preferredEpisodeId = target.Id;
            selectedEpisode = target;
        }
        var wanted = preferredSeasonId ?? selectedSeasonId;
        if (!manualSeason && target?.SeasonId is { } targetSeason && values.Any(s => s.Id == targetSeason)) wanted = targetSeason;
        else if (!values.Any(s => s.Id == wanted)) wanted = item?.Kind == MediaKind.Season && values.Any(s => s.Id == item.Id)
            ? item.Id : values.FirstOrDefault()?.Id;
        if (wanted is not null && selectedSeasonId != wanted) ChangeSeason(wanted);
        if (values.Length > 0) preferredSeasonId = null;
        foreach (var season in Seasons) season.IsSelected = season.Id == selectedSeasonId;
        ApplyEpisodes();
    }

    private void ChangeSeason(string id)
    {
        episodeGeneration++;
        locatingEpisode = false;
        notifiedEpisodeId = null;
        if (episodes is not null) { episodes.Updated -= OnEpisodesUpdated; episodes.Dispose(); }
        selectedSeasonId = id;
        Episodes.Clear();
        episodes = library.ObserveEpisodes(id, 30, scope.Token);
        episodes.Updated += OnEpisodesUpdated;
        foreach (var season in Seasons) season.IsSelected = season.Id == id;
        OnPropertyChanged(nameof(SelectedSeasonId));
        ApplyEpisodes();
    }

    private void ApplyEpisodes()
    {
        if (disposed) return;
        var query = episodes;
        var values = query is null || query.Items.IsDefault ? [] : query.Items;
        var prefix = Episodes.Count <= values.Length;
        for (var i = 0; prefix && i < Episodes.Count; i++)
            if (Episodes[i].Item != values[i]) prefix = false;
        if (!prefix) Episodes.Clear();
        for (var i = prefix ? Episodes.Count : 0; i < values.Length; i++) Episodes.Add(new DetailEpisodeViewModel(values[i]));
        IsEpisodesLoading = query is { IsInitialized: false, Error: null };
        IsLoadingMore = query is { IsInitialized: true, IsLoading: true, IsRefreshing: false };
        HasEpisodesError = query?.Error is not null && Episodes.Count == 0;
        HasEpisodeMoreError = query?.Error is not null && Episodes.Count > 0;
        EpisodeErrorText = query?.Error?.Message ?? "";
        IsEpisodesEmpty = query is { IsInitialized: true, Error: null } && Episodes.Count == 0;
        if (preferredEpisodeId is { } preferred && Episodes.FirstOrDefault(e => e.Id == preferred) is { } loaded)
        {
            selectedEpisode = loaded.Item;
            if (notifiedEpisodeId != loaded.Id)
            {
                notifiedEpisodeId = loaded.Id;
                TargetEpisodeAvailable?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (preferredEpisodeId is null && Episodes.Count > 0) selectedEpisode = Episodes[0].Item;
        else if (query is { IsInitialized: true, HasMore: false, Error: null } && Episodes.Count > 0)
        {
            preferredEpisodeId = null;
            selectedEpisode = Episodes[0].Item;
        }
        else if (query is { IsInitialized: true, HasMore: true, IsLoading: false, Error: null } && !locatingEpisode)
            _ = LocateEpisodeAsync(query, episodeGeneration);
        ApplyEpisodeSelection();
        ApplyHero();
        OnPropertyChanged(nameof(HasMoreEpisodes));
    }

    private async Task LocateEpisodeAsync(IPagedQuery<MediaItem> query, int generation)
    {
        if (preferredEpisodeId is null) return;
        locatingEpisode = true;
        try
        {
            while (!disposed && generation == episodeGeneration && query.HasMore && query.Error is null &&
                !query.Items.Any(e => e.Id == preferredEpisodeId))
            {
                var count = query.Items.Length;
                await query.LoadMoreAsync(scope.Token);
                if (query.Items.Length <= count) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { if (generation == episodeGeneration) locatingEpisode = false; }
    }

    private void ApplyEpisodeSelection()
    {
        foreach (var episode in Episodes) episode.IsSelected = episode.Id == selectedEpisode?.Id;
    }

    private void ApplyHero()
    {
        if (item is null || disposed) return;
        var heading = seriesDetail?.Current ?? item;
        var target = selectedEpisode ?? (item.Kind is MediaKind.Movie or MediaKind.Video or MediaKind.Episode ? item : null);
        Title = heading.Kind == MediaKind.Episode && !string.IsNullOrEmpty(heading.SeriesName) ? heading.SeriesName! : heading.Name;
        Backdrop = ImagePicker.First(item, ImageKind.Backdrop) ?? ImagePicker.First(heading, ImageKind.Backdrop, ImageKind.Primary) ?? ImagePicker.First(item, ImageKind.Primary);
        Logo = ImagePicker.First(heading, ImageKind.Logo) ?? ImagePicker.First(item, ImageKind.Logo);
        Overview = target?.Overview ?? heading.Overview ?? "";
        RatingStar = heading.CommunityRating is > 0 ? "★ " : "";
        Metadata = MediaCardViewModel.JoinParts(heading.CommunityRating is > 0 and var rating ? rating.ToString("0.0", CultureInfo.InvariantCulture) : "",
            MediaCardViewModel.Year(heading), string.Join(" / ", heading.Genres.Take(3)), heading.OfficialRating ?? "");
        EpisodeLine = target?.Kind == MediaKind.Episode
            ? MediaCardViewModel.JoinParts(target.ParentIndexNumber is { } season ? $"第 {season} 季" : "",
                target.IndexNumber is { } episode ? $"第 {episode} 集" : "", target.Name) : "";
        PlayItemId = target?.Id ?? "";
        ProgressFraction = target is null ? 0 : MediaCardViewModel.Progress(target);
        PlayLabel = (ProgressFraction > 0 ? "继续播放" : target?.UserData.Played == true ? "重新播放" : "播放") +
            (target?.Kind == MediaKind.Episode && target.IndexNumber is { } number ? $" 第 {number} 集" : "");
        PlayHint = target is null ? "请选择一集" : ProgressFraction > 0 ? MediaCardViewModel.RemainingText(target) : Duration(target.RunTimeTicks);
        if (target?.UserData.Played == true) PlayHint = MediaCardViewModel.JoinParts("已看完", PlayHint);
        var people = heading.People.IsDefaultOrEmpty ? item.People : heading.People;
        if (!people.Select(p => p.Id).SequenceEqual(People.Select(p => p.Id)) || people.Where((p, index) => p != People[index].Person).Any())
        {
            People.Clear();
            foreach (var person in people) People.Add(new DetailPersonViewModel(person));
        }
        foreach (var property in new[] { nameof(HasLogo), nameof(HasTitle), nameof(HasEpisodeLine), nameof(HasPeople), nameof(HasProgress), nameof(CanPlay), nameof(CanReplay), nameof(SelectedEpisodeId) })
            OnPropertyChanged(property);
    }

    private void DisposeSeries()
    {
        if (seriesDetail is not null) { seriesDetail.Updated -= OnSeriesUpdated; seriesDetail.Dispose(); }
        if (nextUp is not null) { nextUp.Updated -= OnSeriesUpdated; nextUp.Dispose(); }
        if (seasons is not null) { seasons.Updated -= OnSeriesUpdated; seasons.Dispose(); }
        if (episodes is not null) { episodes.Updated -= OnEpisodesUpdated; episodes.Dispose(); }
        seriesDetail = null; nextUp = null; seasons = null; episodes = null;
        selectedSeasonId = null; selectedEpisode = null; preferredEpisodeId = null;
        Seasons.Clear(); Episodes.Clear();
        episodeGeneration++;
    }

    internal static string Duration(long? ticks) => ticks is > 0 ? $"{Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(ticks.Value).TotalMinutes))} 分钟" : "";
}

public sealed partial class DetailSeasonViewModel(SeasonInfo season) : ObservableObject
{
    internal SeasonInfo Season { get; } = season;
    public string Id => Season.Id;
    public string Title => Season.IndexNumber is > 0 and var number ? $"第 {number} 季" : Season.Name;
    [ObservableProperty] public partial bool IsSelected { get; internal set; }
}

public sealed partial class DetailEpisodeViewModel(MediaItem item) : ObservableObject
{
    internal MediaItem Item { get; } = item;
    public string Id => Item.Id;
    public string Title => Item.IndexNumber is { } number ? $"{number}. {Item.Name}" : Item.Name;
    public string Subtitle => MediaCardViewModel.JoinParts(Item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", DetailViewModel.Duration(Item.RunTimeTicks));
    public object? Image => ImagePicker.Landscape(Item);
    public bool Played => Item.UserData.Played;
    public bool HasProgress => MediaCardViewModel.Progress(Item) > 0;
    public GridLength ProgressDone => new(Math.Max(MediaCardViewModel.Progress(Item), 0.0001), GridUnitType.Star);
    public GridLength ProgressRest => new(Math.Max(1 - MediaCardViewModel.Progress(Item), 0.0001), GridUnitType.Star);
    [ObservableProperty] public partial bool IsSelected { get; internal set; }
}

public sealed partial class DetailPersonViewModel(PersonInfo person)
{
    internal PersonInfo Person { get; } = person;
    public string Id => Person.Id;
    public string Name => Person.Name;
    public object? Image => Person.Image;
    public bool HasNoImage => Person.Image is null;
    public string Role => !string.IsNullOrWhiteSpace(Person.Role) ? Person.Role : Person.Kind switch
    {
        PersonKind.Director => "导演", PersonKind.Writer => "编剧", PersonKind.Producer => "制片", PersonKind.Actor => "演员", _ => "演职员",
    };
}
