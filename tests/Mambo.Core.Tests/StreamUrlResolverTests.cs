using System.Net;
using Mambo.Core.Playback;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class StreamUrlResolverTests
{
    [Fact]
    public async Task AuthenticatedServerRedirectPreservesIssuedDownloadQueryWithoutHeadersOrProbe()
    {
        var token = Guid.NewGuid().ToString("N");
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            Assert.Equal("example.invalid", request.RequestUri!.Host);
            Assert.Equal(token, Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            Assert.Equal("bytes=0-0", request.Headers.Range!.ToString());
            return Redirect("https://cdn.example.invalid/video?token=" + token + "&quality=original");
        });
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video?api_key=" + token), new Uri("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Empty(resolved.Headers);
        Assert.Equal("?token=" + token + "&quality=original", resolved.Address.Query);
        Assert.DoesNotContain(token, resolved.ToString());
        Assert.DoesNotContain("example.invalid", resolved.ToString());
    }

    [Theory]
    [InlineData("https://example.invalid:444/video")]
    [InlineData("http://example.invalid/video")]
    [InlineData("https://other.example.invalid/video")]
    public async Task OriginIncludesSchemeHostAndPort(string destination)
    {
        var token = Guid.NewGuid().ToString("N");
        using var handler = new StubHandler(_ => Redirect(destination));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
    }

    [Fact]
    public async Task SameOriginRedirectRetainsHeaderAndResolvesRelativeLocation()
    {
        var token = Guid.NewGuid().ToString("N");
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(token, Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            return ++calls == 1 ? Redirect("/new-video") : new(HttpStatusCode.PartialContent);
        });
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal("/new-video", resolved.Address.AbsolutePath);
        Assert.Equal(token, resolved.Headers["X-Emby-Token"]);
    }

    [Fact]
    public async Task RejectedRangeFallsBackToHeadThenPlainGet()
    {
        var methods = new List<string>();
        using var handler = new StubHandler(request =>
        {
            methods.Add(request.Method.Method + (request.Headers.Range is not null ? "/Range" : ""));
            return new(methods.Count < 3 ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(["GET/Range", "HEAD", "GET"], methods);
    }

    [Fact]
    public async Task RedirectLoopIsBounded()
    {
        var calls = 0;
        using var handler = new StubHandler(_ => { calls++; return Redirect("/video"); });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken));
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task UnauthenticatedCrossOriginCandidateIsNotProbed()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("不应发出请求。"));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://cdn.example.invalid/video"), new Uri("https://example.invalid"), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
    }

    [Fact]
    public async Task CdnSignatureIsPreservedWithoutEmbyHeaders()
    {
        var token = Guid.NewGuid().ToString("N");
        var signature = Guid.NewGuid().ToString("N");
        using var handler = new StubHandler(_ => Redirect("https://cdn.example.invalid/video?token=" + signature));
        using var client = new HttpClient(handler);
        var result = await new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Empty(result.Headers);
        Assert.Equal("?token=" + signature, result.Address.Query);
    }

    [Fact]
    public async Task NetworkErrorsDoNotExposeAddress()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("https://example.invalid/private"));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new StreamUrlResolver(client).ResolveAsync(
            new Uri("https://example.invalid/video"), new Uri("https://example.invalid"), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("example.invalid", error.Message);
    }

    [Theory]
    [InlineData("/emby-other/video")]
    [InlineData("/other/video")]
    [InlineData("/emby%2fvideo")]
    [InlineData("/emby/%2e%2e/private")]
    [InlineData("/EMBY/video")]
    public async Task AuthenticationBoundaryIncludesExactBasePath(string path)
    {
        var host = Guid.NewGuid().ToString("N") + ".invalid";
        var token = Guid.NewGuid().ToString("N");
        using var handler = new StubHandler(_ => throw new InvalidOperationException("越界地址不应探测"));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(new("https://" + host + path),
            new("https://" + host + "/emby"), token, new Dictionary<string, string> { ["Referer"] = token }, TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
    }

    [Fact]
    public async Task SameHostRedirectOutsideBasePathClearsAllRequiredHeadersAndStopsProbe()
    {
        var host = Guid.NewGuid().ToString("N") + ".invalid";
        var token = Guid.NewGuid().ToString("N"); var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++; Assert.Equal(token, request.Headers.GetValues("X-Emby-Token").Single());
            Assert.True(request.Headers.Contains("Referer")); return Redirect("/outside/video?api_key=" + token);
        });
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(new("https://" + host + "/emby/video"),
            new("https://" + host + "/emby"), token, new Dictionary<string, string> { ["Referer"] = Guid.NewGuid().ToString("N") }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Empty(resolved.Headers); Assert.Equal("?api_key=" + token, resolved.Address.Query);
    }

    [Fact]
    public async Task EncodedIdentifierInsideBasePathRetainsAuthentication()
    {
        var root = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby");
        var token = Guid.NewGuid().ToString("N"); var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++; Assert.Equal(token, request.Headers.GetValues("X-Emby-Token").Single());
            return new(HttpStatusCode.PartialContent);
        });
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(new(root.AbsoluteUri + "/Videos/" + Guid.NewGuid().ToString("N") + "%2Fpart/stream"),
            root, token, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(token, resolved.Headers["X-Emby-Token"]);
    }

    [Fact]
    public async Task ValidRequiredHeadersRetainedAndInjectionCannotOverrideAuthentication()
    {
        var root = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby");
        var token = Guid.NewGuid().ToString("N");
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "origin", ["X-Emby-Token"] = Guid.NewGuid().ToString("N"), ["X-Bad"] = "one\r\nInjected: two", ["Bad Key"] = "three", ["Host"] = "other",
        };
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(token, request.Headers.GetValues("X-Emby-Token").Single());
            Assert.Equal("origin", request.Headers.GetValues("Referer").Single());
            Assert.False(request.Headers.Contains("X-Bad")); Assert.Null(request.Headers.Host);
            return new(HttpStatusCode.PartialContent);
        });
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(new(root.AbsoluteUri + "/video"), root, token, headers, TestContext.Current.CancellationToken);
        Assert.Equal(2, resolved.Headers.Count);
    }

    [Fact]
    public async Task SubtitleDownloadRechecksRedirectAfterProbeAndNeverRestoresClearedHeaders()
    {
        var root = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby");
        var cdn = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/subtitle");
        var token = Guid.NewGuid().ToString("N"); var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            if (calls <= 2)
            {
                Assert.Equal(token, request.Headers.GetValues("X-Emby-Token").Single());
                Assert.True(request.Headers.Contains("Referer"));
                return calls == 1 ? new(HttpStatusCode.PartialContent) : Redirect(cdn.AbsoluteUri + "?token=" + token);
            }
            Assert.False(request.Headers.Contains("X-Emby-Token")); Assert.False(request.Headers.Contains("Referer"));
            if (calls == 3) Assert.Equal("?token=" + token, request.RequestUri!.Query);
            else Assert.DoesNotContain(token, request.RequestUri!.Query, StringComparison.Ordinal);
            return calls == 3 ? Redirect(root.AbsoluteUri + "/final") : new(HttpStatusCode.OK) { Content = new StringContent("字幕") };
        });
        using var client = new HttpClient(handler);
        var bytes = await new StreamUrlResolver(client).DownloadSubtitleAsync(new(root.AbsoluteUri + "/subtitle"), root, token,
            new Dictionary<string, string> { ["Referer"] = root.AbsoluteUri }, TestContext.Current.CancellationToken);
        Assert.Equal("字幕", System.Text.Encoding.UTF8.GetString(bytes)); Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SubtitleDownloadRejectsOversizedResponseWithoutReadingBody()
    {
        var root = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby");
        using var handler = new StubHandler(request =>
        {
            if (request.Headers.Range is not null) return new(HttpStatusCode.PartialContent);
            var content = new ByteArrayContent([]); content.Headers.ContentLength = 5 * 1024 * 1024 + 1;
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StreamUrlResolver(client).DownloadSubtitleAsync(new(root.AbsoluteUri + "/subtitle"), root,
            Guid.NewGuid().ToString("N"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlreadyCancelledCrossOriginResolutionDoesNotSucceed()
    {
        var address = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/video");
        var root = new Uri("https://" + Guid.NewGuid().ToString("N") + ".invalid/emby");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var handler = new StubHandler(_ => throw new InvalidOperationException("取消后不应请求"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StreamUrlResolver(client).ResolveAsync(address, root,
            Guid.NewGuid().ToString("N"), cancellation.Token));
    }

    [Fact]
    public async Task DirectCrossOriginAddressCannotCopyAccountCredential()
    {
        var token = Guid.NewGuid().ToString("N");
        using var handler = new StubHandler(_ => throw new InvalidOperationException("不应探测非认证地址"));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new("https://cdn.example.invalid/video?token=" + token + "&quality=original"),
            new("https://example.invalid"), token, new Dictionary<string, string> { ["Authorization"] = token }, TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
        Assert.Equal("?quality=original", resolved.Address.Query);
    }

    [Fact]
    public async Task AuthenticatedRedirectDoesNotPermitCredentialOnHttpsDowngrade()
    {
        var token = Guid.NewGuid().ToString("N");
        using var handler = new StubHandler(_ => Redirect("http://cdn.example.invalid/video?api_key=" + token));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new("https://example.invalid/video"), new("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
        Assert.Empty(resolved.Address.Query);
    }

    [Fact]
    public async Task IssuedDownloadQueryIsNotRewrittenOrAugmented()
    {
        var token = Guid.NewGuid().ToString("N");
        var query = "?api_key=" + token + "&sig=a%2Fb%2Bc+z&flag&&repeat=1&repeat=2";
        using var handler = new StubHandler(_ => Redirect("https://cdn.example.invalid/video" + query));
        using var client = new HttpClient(handler);
        var resolved = await new StreamUrlResolver(client).ResolveAsync(
            new("https://example.invalid/video"), new("https://example.invalid"), token, TestContext.Current.CancellationToken);
        Assert.Empty(resolved.Headers);
        Assert.Equal(query, resolved.Address.Query);
    }

    [Fact]
    public async Task SubtitleRedirectFromCdnCannotAcquireServerTrustOrRestoreHeaders()
    {
        var token = Guid.NewGuid().ToString("N");
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            if (calls == 1) return Redirect("https://cdn.example.invalid/subtitle?token=" + token);
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            Assert.False(request.Headers.Contains("Authorization"));
            if (calls == 2)
            {
                Assert.Contains(token, request.RequestUri!.Query, StringComparison.Ordinal);
                return Redirect("https://other.example.invalid/subtitle?token=" + token);
            }
            Assert.DoesNotContain(token, request.RequestUri!.Query, StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = new StringContent("字幕") };
        });
        using var client = new HttpClient(handler);
        var bytes = await new StreamUrlResolver(client).DownloadSubtitleAsync(
            new("https://example.invalid/subtitle"), new("https://example.invalid"), token,
            new Dictionary<string, string> { ["Authorization"] = token }, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
        Assert.Equal("字幕", System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static HttpResponseMessage Redirect(string address) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(address, UriKind.RelativeOrAbsolute) },
    };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
