using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Networking;

public sealed class EmbyApi : IDisposable
{
    public const string ItemFields = "Genres,CommunityRating,OfficialRating,RunTimeTicks,PremiereDate,DateCreated,ProductionYear,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentBackdropItemId,ParentBackdropImageTags,SortName";
    public const string DetailFields = ItemFields + ",Overview,MediaSources,People,ParentLogoItemId,ParentLogoImageTag";
    public const string LatestFields = "Overview,Genres,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentLogoItemId,ParentLogoImageTag,ParentBackdropItemId,ParentBackdropImageTags";
    public const string ResumeFields = "Overview,Genres,MediaSources,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentLogoItemId,ParentLogoImageTag,ParentBackdropItemId,ParentBackdropImageTags";
    private readonly HttpClient client;
    private readonly Guid deviceId;
    private readonly string version;
    public event Action<AccountSession>? AuthenticationExpired;
    public EmbyApi(Guid deviceId, HttpMessageHandler? handler = null, string version = "0.1.0")
    {
        this.deviceId = deviceId; this.version = version;
        client = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
    }
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    public Task<EmbyAuthentication> AuthenticateAsync(ServerAddress address, LoginRequest request, CancellationToken token) => SendJsonAsync(address, null, "Users/AuthenticateByName", HttpMethod.Post,
        JsonSerializer.SerializeToUtf8Bytes(new AuthenticationRequest(request.UserName.Trim(), request.Password), EmbyJsonContext.Default.AuthenticationRequest), EmbyJsonContext.Default.EmbyAuthentication, "登录", token);
    public Task<EmbyItems> ItemsAsync(AccountSession account, string path, CancellationToken token) => SendJsonAsync(account.Address, account, path, HttpMethod.Get, null, EmbyJsonContext.Default.EmbyItems, "加载媒体资料", token);
    public Task<EmbyItem> DetailAsync(AccountSession account, string id, CancellationToken token) => SendJsonAsync(account.Address, account,
        "Users/" + Escape(account.Secret.UserId) + "/Items/" + Escape(id) + "?Fields=" + Escape(DetailFields), HttpMethod.Get, null, EmbyJsonContext.Default.EmbyItem, "加载详情", token);
    public Task<EmbyItem[]> LatestAsync(AccountSession account, string libraryId, CancellationToken token) => SendJsonAsync(account.Address, account,
        "Users/" + Escape(account.Secret.UserId) + "/Items/Latest?ParentId=" + Escape(libraryId) + "&Limit=16&Fields=" + Escape(LatestFields), HttpMethod.Get, null, EmbyJsonContext.Default.EmbyItemArray, "加载最新内容", token);
    public Task<EmbyFilters> FiltersAsync(AccountSession account, string libraryId, CancellationToken token) => SendJsonAsync(account.Address, account,
        "Items/Filters?UserId=" + Escape(account.Secret.UserId) + "&ParentId=" + Escape(libraryId) + "&IncludeItemTypes=Movie,Series,Video", HttpMethod.Get, null, EmbyJsonContext.Default.EmbyFilters, "加载筛选项", token, TimeSpan.FromSeconds(3));
    public Task ValidateAsync(AccountSession account, CancellationToken token) => SendStatusAsync(account, "Users/" + Escape(account.Secret.UserId), HttpMethod.Get, null, "恢复会话", token);
    public Task LogoutAsync(AccountSession account, CancellationToken token) => SendStatusAsync(account, "Sessions/Logout", HttpMethod.Post, null, "注销", token, logout: true);
    public Task<EmbyPlaybackInfo> PlaybackInfoAsync(AccountSession account, string id, long start, CancellationToken token) => SendJsonAsync(account.Address, account,
        "Items/" + Escape(id) + "/PlaybackInfo?UserId=" + Escape(account.Secret.UserId), HttpMethod.Post,
        JsonSerializer.SerializeToUtf8Bytes(new PlaybackInfoRequest(account.Secret.UserId, start, new DeviceProfile()), EmbyJsonContext.Default.PlaybackInfoRequest), EmbyJsonContext.Default.EmbyPlaybackInfo, "准备播放", token);
    public Task ReportAsync(AccountSession account, string kind, PlaybackReport report, CancellationToken token) => SendStatusAsync(account,
        "Sessions/Playing" + (kind.Length == 0 ? "" : "/" + kind) + "?reqformat=json", HttpMethod.Post,
        JsonSerializer.SerializeToUtf8Bytes(report, EmbyJsonContext.Default.PlaybackReport), "上报播放", token);
    public Task CleanupAsync(AccountSession account, string playSessionId, CancellationToken token) => SendStatusAsync(account,
        "Videos/ActiveEncodings?DeviceId=" + deviceId.ToString("D") + "&PlaySessionId=" + Escape(playSessionId), HttpMethod.Delete, null, "清理转码", token);
    private async Task<T> SendJsonAsync<T>(ServerAddress address, AccountSession? account, string path, HttpMethod method, byte[]? body, JsonTypeInfo<T> type, string stage, CancellationToken token, TimeSpan? timeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var request = Create(address, account, path, method, body);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            Check(response, account, stage);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, 16 * 1024 * 1024, linked.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize(bytes, type) ?? throw new AppException(ErrorText.InvalidResponse(stage));
        }
        catch (HttpRequestException) { throw new AppException(ErrorText.Network(stage)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AppException(ErrorText.Network(stage)); }
        catch (JsonException) { throw new AppException(ErrorText.InvalidResponse(stage)); }
        catch (IOException) { throw new AppException(ErrorText.Network(stage)); }
    }
    private async Task SendStatusAsync(AccountSession account, string path, HttpMethod method, byte[]? body, string stage, CancellationToken token, bool logout = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = Create(account.Address, account, path, method, body);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!logout || response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) Check(response, account, stage);
        }
        catch (HttpRequestException) { throw new AppException(ErrorText.Network(stage)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AppException(ErrorText.Network(stage)); }
    }
    private HttpRequestMessage Create(ServerAddress address, AccountSession? account, string path, HttpMethod method, byte[]? body)
    {
        var request = new HttpRequestMessage(method, address.Endpoint(path)) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        AuthHeader.Apply(request, deviceId, version, account);
        if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
        return request;
    }
    private void Check(HttpResponseMessage response, AccountSession? account, string stage)
    {
        if (response.IsSuccessStatusCode) return;
        if (account is not null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) AuthenticationExpired?.Invoke(account);
        throw new AppException(ErrorText.Http((int)response.StatusCode, stage));
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > limit) throw new AppException(ErrorText.InvalidResponse("下载数据"));
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    public static string Escape(string value) => Uri.EscapeDataString(value);
    public void Dispose() { AuthenticationExpired = null; client.Dispose(); }
}
