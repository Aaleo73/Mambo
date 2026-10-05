using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.Core.Contracts;
using Microsoft.UI.Dispatching;

namespace Mambo.App.ViewModels;

/// <summary>搜索结果里的一行：作品或剧集，二者必居其一。</summary>
public sealed class BulletChatRow(string title, string detail, BulletChatAnime? anime, BulletChatEpisode? episode)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public bool HasDetail => Detail.Length > 0;
    internal BulletChatAnime? Anime { get; } = anime;
    internal BulletChatEpisode? Episode { get; } = episode;
}

/// <summary>播放页弹幕面板：样式调节、匹配状态与手动搜索。只依赖 Contracts。</summary>
public sealed partial class BulletChatPanelViewModel : ObservableObject, IDisposable
{
    private static readonly double[] SpeedSeconds = [24, 19, 15, 11, 8];
    private static readonly string[] SpeedNames = ["极慢", "慢", "标准", "快", "极快"];
    private readonly IBulletChatService service;
    private readonly ISettingsService settings;
    private readonly Func<string?> suggestion;
    private readonly DispatcherQueueTimer saveTimer;
    private CancellationTokenSource? work;
    private BulletChatAnime? openedAnime;
    private bool synchronizing;
    private bool dirty;
    private bool disposed;

    public BulletChatPanelViewModel(IBulletChatService service, ISettingsService settings, Func<string?> suggestion, DispatcherQueue queue)
    {
        this.service = service; this.settings = settings; this.suggestion = suggestion;
        saveTimer = queue.CreateTimer();
        saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += OnSaveTick;
        service.Changed += OnServiceChanged;
        settings.Changed += OnSettingsChanged;
        Refresh();
    }

    /// <summary>滑块拖动中的即时预览；稍后才写入设置。</summary>
    public event EventHandler<BulletChatSettings>? PreviewChanged;
    /// <summary>手动选定了剧集，面板可以收起。</summary>
    public event EventHandler? Completed;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    public partial double OpacityPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontText))]
    public partial double FontPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double SpeedLevel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreaText))]
    public partial double AreaPercent { get; set; }

    public string OpacityText => OpacityPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
    public string FontText => FontPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
    public string SpeedText => SpeedNames[SpeedIndex];
    public string AreaText => AreaPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
    private int SpeedIndex => Math.Clamp((int)Math.Round(SpeedLevel), 0, SpeedSeconds.Length - 1);

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanSearch { get; private set; }

    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsView))]
    public partial bool IsSearchView { get; private set; }
    public bool IsSettingsView => !IsSearchView;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeading))]
    public partial string Heading { get; private set; } = "";
    public bool HasHeading => Heading.Length > 0;

    public ObservableCollection<BulletChatRow> Rows { get; } = [];

    partial void OnEnabledChanged(bool value)
    {
        if (synchronizing) return;
        // 开关立即生效：服务据此开始或停止加载。
        saveTimer.Stop();
        dirty = true;
        _ = SaveAsync();
    }
    partial void OnOpacityPercentChanged(double value) => Edited();
    partial void OnFontPercentChanged(double value) => Edited();
    partial void OnSpeedLevelChanged(double value) => Edited();
    partial void OnAreaPercentChanged(double value) => Edited();

    private void Edited()
    {
        if (synchronizing || disposed) return;
        dirty = true;
        PreviewChanged?.Invoke(this, Build());
        saveTimer.Stop();
        saveTimer.Start();
    }

    private BulletChatSettings Build() => settings.Current.BulletChat with
    {
        Enabled = Enabled,
        Opacity = Math.Clamp(OpacityPercent / 100, 0.2, 1),
        FontScale = Math.Clamp(FontPercent / 100, 0.6, 2),
        ScrollSeconds = SpeedSeconds[SpeedIndex],
        Area = Math.Clamp(AreaPercent / 100, 0.1, 1),
    };

    private void OnSaveTick(DispatcherQueueTimer sender, object args) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (disposed || !dirty) return;
        var built = Build();
        try
        {
            await settings.UpdateAsync(value => value with { BulletChat = built });
            // 保存期间用户没有继续改动，才认为界面与设置一致。
            if (built == Build()) dirty = false;
        }
        catch (AppException error) { if (!disposed) Message = error.Error.Message; }
        if (!disposed) Refresh();
    }

    /// <summary>面板收起：把还没落盘的改动写下去，并回到样式视图。</summary>
    public void Close()
    {
        if (disposed) return;
        Cancel();
        if (saveTimer.IsRunning) { saveTimer.Stop(); _ = SaveAsync(); }
        IsSearchView = false;
        IsBusy = false;
        Message = "";
    }

    private void OnServiceChanged(object? sender, EventArgs args) => Refresh();
    private void OnSettingsChanged(object? sender, EventArgs args) => Refresh();

    public void Refresh()
    {
        if (disposed) return;
        var stored = settings.Current.BulletChat;
        // 有未保存的改动时不让旧的设置通知把滑块拉回去。
        if (!dirty)
        {
            synchronizing = true;
            try
            {
                Enabled = stored.Enabled;
                OpacityPercent = Math.Round(stored.Opacity * 100);
                FontPercent = Math.Round(stored.FontScale * 100);
                AreaPercent = Math.Round(stored.Area * 100);
                var nearest = 0;
                for (var index = 1; index < SpeedSeconds.Length; index++)
                    if (Math.Abs(SpeedSeconds[index] - stored.ScrollSeconds) < Math.Abs(SpeedSeconds[nearest] - stored.ScrollSeconds)) nearest = index;
                SpeedLevel = nearest;
            }
            finally { synchronizing = false; }
        }
        var state = service.Current;
        StatusText = state.Status switch
        {
            BulletChatStatus.Loading => "正在匹配",
            BulletChatStatus.Loaded when state.Episode is { } episode => (episode.AnimeTitle + " " + episode.Title).Trim(),
            BulletChatStatus.NotMatched => "未匹配到弹幕",
            BulletChatStatus.Failed => state.Error?.Message ?? "弹幕加载失败",
            _ => stored.Enabled ? "" : "已关闭",
        };
        CanSearch = state.Status is BulletChatStatus.Loaded or BulletChatStatus.NotMatched or BulletChatStatus.Failed;
        CanRetry = state.Status == BulletChatStatus.Failed;
    }

    public void Retry()
    {
        if (disposed) return;
        try { _ = service.ReloadAsync(); }
        catch (ObjectDisposedException) { }
    }

    public void OpenSearch()
    {
        if (disposed) return;
        IsSearchView = true;
        openedAnime = null;
        Heading = "";
        Message = "";
        Rows.Clear();
        if (SearchText.Trim().Length == 0) SearchText = suggestion() ?? "";
        if (SearchText.Trim().Length > 0) _ = SearchAsync();
    }

    public async Task SearchAsync()
    {
        var keyword = SearchText.Trim();
        if (disposed || keyword.Length == 0) return;
        var token = Begin();
        try
        {
            var animes = await service.SearchAsync(keyword, token);
            if (token.IsCancellationRequested) return;
            openedAnime = null;
            Heading = "";
            Rows.Clear();
            foreach (var anime in animes)
            {
                string?[] parts =
                [
                    anime.TypeLabel, anime.Year?.ToString(CultureInfo.InvariantCulture),
                    anime.EpisodeCount > 0 ? anime.EpisodeCount.ToString(CultureInfo.InvariantCulture) + " 集" : null,
                ];
                Rows.Add(new(anime.Title, string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part))), anime, null));
            }
            Message = Rows.Count == 0 ? "没有结果" : "";
        }
        catch (OperationCanceledException) { }
        catch (AppException error) { if (!token.IsCancellationRequested) Message = error.Error.Message; }
        finally { End(token); }
    }

    public async Task OpenAsync(BulletChatRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (disposed) return;
        var token = Begin();
        try
        {
            if (row.Episode is { } episode)
            {
                await service.SelectAsync(episode, token);
                if (token.IsCancellationRequested) return;
                IsSearchView = false;
                Completed?.Invoke(this, EventArgs.Empty);
            }
            else if (row.Anime is { } anime)
            {
                var episodes = await service.GetEpisodesAsync(anime.Id, token);
                if (token.IsCancellationRequested) return;
                openedAnime = anime;
                Heading = anime.Title;
                Rows.Clear();
                foreach (var item in episodes) Rows.Add(new(item.Title.Length > 0 ? item.Title : item.Id, "", null, item));
                Message = Rows.Count == 0 ? "没有结果" : "";
            }
        }
        catch (OperationCanceledException) { }
        catch (AppException error) { if (!token.IsCancellationRequested) Message = error.Error.Message; }
        finally { End(token); }
    }

    /// <summary>从剧集列表回到作品列表，再回到样式视图。</summary>
    public void Back()
    {
        if (disposed) return;
        Cancel();
        if (openedAnime is not null) { openedAnime = null; _ = SearchAsync(); }
        else { IsSearchView = false; Message = ""; }
    }

    private CancellationToken Begin()
    {
        Cancel();
        work = new CancellationTokenSource();
        IsBusy = true;
        Message = "";
        return work.Token;
    }

    private void End(CancellationToken token)
    {
        // 被后一次操作取代的旧操作不能把"忙"状态关掉。
        if (work is { } current && current.Token == token)
        {
            work = null;
            current.Dispose();
            if (!disposed) IsBusy = false;
        }
    }

    private void Cancel()
    {
        if (work is not { } current) return;
        work = null;
        current.Cancel();
        current.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel();
        saveTimer.Stop();
        saveTimer.Tick -= OnSaveTick;
        service.Changed -= OnServiceChanged;
        settings.Changed -= OnSettingsChanged;
        PreviewChanged = null;
        Completed = null;
    }
}
