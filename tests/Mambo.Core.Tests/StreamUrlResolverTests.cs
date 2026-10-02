using System.Net;
using Mambo.Core.Playback;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class StreamUrlResolverTests
{
    [Fact]
    public async Task CrossOriginRedirectRemovesTokenAndDoesNotContactDestination()
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
        Assert.Equal("?quality=original", resolved.Address.Query);
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
