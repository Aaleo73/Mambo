using System.Net;
using System.Net.Http.Headers;

namespace Mambo.Core.Playback;

/// <summary>含临时认证信息；只允许在内存中使用。</summary>
public sealed class ResolvedPlaybackUrl(Uri address, IReadOnlyDictionary<string, string> headers, int redirects)
{
    public Uri Address { get; } = address;
    public IReadOnlyDictionary<string, string> Headers { get; } = headers;
    public int Redirects { get; } = redirects;
    public override string ToString() => $"播放地址已隐藏；重定向 {Redirects} 次";
}

/// <summary>预解析认证请求；跨源跳转立即结束探测并清除认证头。</summary>
public sealed class StreamUrlResolver(HttpClient client)
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ResolvedPlaybackUrl> ResolveAsync(
        Uri address, Uri serverOrigin, string? token, CancellationToken cancellationToken = default)
    {
        ValidateAddress(address);
        ValidateAddress(serverOrigin);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!SameOrigin(address, serverOrigin))
            return new ResolvedPlaybackUrl(RemoveCopiedCredential(address, token), headers, 0);
        if (string.IsNullOrWhiteSpace(token))
            return new ResolvedPlaybackUrl(RemoveAuthenticationQuery(address), headers, 0);
        if (token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("令牌包含无效字符。", nameof(token));
        headers.Add("X-Emby-Token", token);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(6));
        var current = RemoveAuthenticationQuery(address);
        try
        {
            for (var hop = 0; hop <= 5; hop++)
            {
                using var response = await ProbeAsync(current, headers, total.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return new ResolvedPlaybackUrl(current, headers, hop);
                if (!IsRedirect(response.StatusCode))
                    throw new InvalidOperationException($"片源探测失败（HTTP {(int)response.StatusCode}）。");
                if (hop == 5)
                    throw new InvalidOperationException("片源重定向次数超过限制。");
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("片源重定向缺少目标地址。");
                current = new Uri(current, location);
                ValidateAddress(current);
                if (!SameOrigin(current, serverOrigin))
                    return new ResolvedPlaybackUrl(RemoveCopiedCredential(current, token), new Dictionary<string, string>(), hop + 1);
                current = RemoveAuthenticationQuery(current);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("片源地址探测超时。");
        }
        catch (HttpRequestException)
        {
            // 网络异常的原始 Message 可能带 URL，因此不保留它或 InnerException。
            throw new InvalidOperationException("无法连接片源服务器。");
        }
        throw new InvalidOperationException("无法解析片源地址。");
    }

    private async Task<HttpResponseMessage> ProbeAsync(
        Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        foreach (var mode in new[] { 0, 1, 2 })
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(mode == 1 ? HttpMethod.Head : HttpMethod.Get, address);
            if (mode == 0) request.Headers.Range = new RangeHeaderValue(0, 0);
            foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
            if (mode == 2 || response.StatusCode is not
                (HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or
                 HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.NotImplemented))
                return response;
            response.Dispose();
        }
        throw new InvalidOperationException("片源探测失败。");
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static void ValidateAddress(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("播放地址必须是无内嵌凭据的 HTTP 或 HTTPS 地址。");
    }

    public static Uri RemoveAuthenticationQuery(Uri address)
    {
        var builder = new UriBuilder(address) { Fragment = "" };
        builder.Query = string.Join("&", address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => Uri.UnescapeDataString(part.Split('=')[0]).ToLowerInvariant()
                is not ("api_key" or "access_token" or "token" or "x-emby-token")));
        return builder.Uri;
    }

    private static Uri RemoveCopiedCredential(Uri address, string? credential)
    {
        // CDN 自己的签名参数必须保留，只去掉从 Emby 复制到 Location 的同一个令牌。
        var builder = new UriBuilder(address) { Fragment = "" };
        builder.Query = string.Join("&", address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var pair = part.Split('=', 2);
                var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
                return key is not ("api_key" or "access_token" or "token" or "x-emby-token") ||
                    pair.Length < 2 || string.IsNullOrEmpty(credential) ||
                    !Uri.UnescapeDataString(pair[1]).Equals(credential, StringComparison.Ordinal);
            }));
        return builder.Uri;
    }
}
