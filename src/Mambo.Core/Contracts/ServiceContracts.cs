using System.Collections.Immutable;

namespace Mambo.Core.Contracts;

/// <summary>
/// 创建观察后开始加载，scopeToken 属于页面或账号生命周期。
/// 释放查询结束当前观察；首页与搜索的各分区独立失败、独立重试。
/// 分页大小须为 1..500。搜索规范化与单集折叠为剧集由后端负责。
/// 观察捕获创建时的账号；切换账号或 scopeToken 取消后终止读取，仍需 Dispose 退订。
/// 有缓存时可在创建时已初始化并后台刷新；播放停止后由后端刷新受影响的活动观察。
/// </summary>
public interface ILibraryService
{
    /// <summary>只返回可播放的视频库，保留服务器返回的顺序。</summary>
    IQuery<ImmutableArray<MediaLibrary>> ObserveLibraries(CancellationToken scopeToken = default);
    IQuery<ImmutableArray<MediaItem>> ObserveHero(CancellationToken scopeToken = default);
    IQuery<ImmutableArray<MediaItem>> ObserveContinueWatching(CancellationToken scopeToken = default);
    IQuery<ImmutableArray<MediaItem>> ObserveLatest(string libraryId, CancellationToken scopeToken = default);
    IPagedQuery<MediaItem> ObserveRecent(int pageSize = 120, CancellationToken scopeToken = default);
    IPagedQuery<MediaItem> ObserveLibrary(string libraryId, LibraryQuery query, int pageSize = 60, CancellationToken scopeToken = default);
    IQuery<FilterOptions> ObserveFilters(string libraryId, CancellationToken scopeToken = default);
    IQuery<MediaItem> ObserveDetail(string itemId, CancellationToken scopeToken = default);
    /// <summary>遵循续播、未看、首集规则；没有可播放集时 Current=null，IsInitialized=true。</summary>
    IQuery<MediaItem> ObserveNextUp(string seriesId, CancellationToken scopeToken = default);
    IQuery<ImmutableArray<SeasonInfo>> ObserveSeasons(string seriesId, CancellationToken scopeToken = default);
    IPagedQuery<MediaItem> ObserveEpisodes(string seasonId, int pageSize = 30, CancellationToken scopeToken = default);
    IQuery<ImmutableArray<SearchGroup>> ObserveSearchGroups(CancellationToken scopeToken = default);
    IPagedQuery<MediaItem> ObserveSearch(string libraryId, string searchText, int pageSize = 24, CancellationToken scopeToken = default);
    /// <summary>悬停预热详情；后台优先级，失败不通知界面，不要求创建或释放观察。</summary>
    void PrefetchDetail(string itemId);
}

public interface IImageService
{
    /// <summary>返回压缩图片字节，服务不再修改该内存；主动取消抛出 OperationCanceledException。</summary>
    Task<ReadOnlyMemory<byte>> FetchAsync(ImageRef image, int pixelWidth, ImagePriority priority = ImagePriority.Visible, CancellationToken cancellationToken = default);
}
