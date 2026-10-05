using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;

namespace Mambo.Core.BulletChat;

/// <summary>弹弹play 兼容协议的只读客户端。请求只携带片名、集号与弹幕库编号，不含任何 Emby 信息。</summary>
public sealed class DandanplayClient : IDisposable
{
    /// <summary>内置的弹幕服务器；失效时改这里并重新构建。</summary>
    public const string DefaultServer = "https://danmaku-api.152468.xyz";
    private static readonly TimeSpan CommentLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromHours(24);
    private readonly HttpClient client;
    private readonly BulletChatCache? cache;
    private readonly string server;

    public DandanplayClient(HttpMessageHandler? handler = null, BulletChatCache? cache = null, string version = "0.1.0", string server = DefaultServer)
    {
        this.cache = cache;
        this.server = server.TrimEnd('/');
        client = new HttpClient(handler ?? EmbyApi.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mambo", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<DandanSearchResponse> SearchAnimeAsync(string keyword, CancellationToken cancellationToken) =>
        SendAsync("search:" + keyword, CatalogLifetime, HttpMethod.Get, "/api/v2/search/anime?keyword=" + Uri.EscapeDataString(keyword), null,
            DandanJsonContext.Default.DandanSearchResponse, "搜索弹幕", cancellationToken);

    public Task<DandanBangumiResponse> BangumiAsync(string animeId, CancellationToken cancellationToken) =>
        SendAsync("bangumi:" + animeId, CatalogLifetime, HttpMethod.Get, "/api/v2/bangumi/" + Uri.EscapeDataString(animeId), null,
            DandanJsonContext.Default.DandanBangumiResponse, "加载弹幕剧集", cancellationToken);

    /// <summary>
    /// 弹幕一律请服务器转成简体（chConvert=1）：来自巴哈姆特等来源的弹幕是繁体，占比可以接近一半。
    /// 服务器按词转换，比逐字映射准确（終於→终于、怎麼→怎么、乾洗→干洗）。
    /// </summary>
    public Task<DandanCommentResponse> CommentsAsync(string episodeId, CancellationToken cancellationToken) =>
        // 缓存键带上转换方式：此前缓存的未转换响应不能再用。
        SendAsync("comment:simplified:" + episodeId, CommentLifetime, HttpMethod.Get, "/api/v2/comment/" + Uri.EscapeDataString(episodeId) + "?withRelated=true&chConvert=1", null,
            DandanJsonContext.Default.DandanCommentResponse, "加载弹幕", cancellationToken);

    /// <summary>按文件名模糊匹配。接口要求附带 32 位十六进制的文件哈希，这里没有真实文件，用文件名摘要的前半段占位。</summary>
    public Task<DandanMatchResponse> MatchAsync(string fileName, CancellationToken cancellationToken)
    {
        var placeholder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName)), 0, 16).ToLowerInvariant();
        var body = JsonSerializer.SerializeToUtf8Bytes(new DandanMatchRequest(fileName, placeholder, "hashAndFileName"), DandanJsonContext.Default.DandanMatchRequest);
        return SendAsync("match:" + fileName, CatalogLifetime, HttpMethod.Post, "/api/v2/match", body,
            DandanJsonContext.Default.DandanMatchResponse, "匹配弹幕", cancellationToken);
    }

    private async Task<T> SendAsync<T>(string cacheKey, TimeSpan lifetime, HttpMethod method, string path, byte[]? body, JsonTypeInfo<T> type, string stage, CancellationToken token)
    {
        if (cache?.TryRead(cacheKey, lifetime) is { } stored)
        {
            try { if (JsonSerializer.Deserialize(stored, type) is { } hit) return hit; }
            catch (JsonException) { /* 缓存损坏：按未命中重新请求。 */ }
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(method, server + path);
            if (body is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AppException(Unavailable(stage, (int)response.StatusCode));
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var bytes = await EmbyApi.ReadBoundedAsync(stream, 16 * 1024 * 1024, linked.Token).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize(bytes, type) ?? throw new AppException(ErrorText.InvalidResponse(stage));
            if (cache is not null) await cache.WriteAsync(cacheKey, bytes, token).ConfigureAwait(false);
            return value;
        }
        catch (HttpRequestException) { throw new AppException(Unreachable(stage)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AppException(Unreachable(stage)); }
        catch (JsonException) { throw new AppException(ErrorText.InvalidResponse(stage)); }
        catch (IOException) { throw new AppException(Unreachable(stage)); }
    }

    private static AppError Unreachable(string stage) =>
        new(AppErrorKind.Network, ErrorCodes.NetworkUnavailable, stage + "失败：无法连接弹幕服务器。", true, stage);
    private static AppError Unavailable(string stage, int status) =>
        new(AppErrorKind.Server, "http." + status, stage + "失败：弹幕服务器暂时无法完成请求。", status is 408 or 425 or 429 || status >= 500, stage, status);

    public void Dispose() => client.Dispose();
}
