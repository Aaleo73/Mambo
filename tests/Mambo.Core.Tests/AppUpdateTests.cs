using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Updates;
using Xunit;

namespace Mambo.Core.Tests;

public sealed class AppUpdateTests
{
    private const string Repository = "example/Mambo";
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("fixture installer, never executable");
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Installer));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.1.0", "0.1.0-beta.1", true)]
    [InlineData("0.2.0", "0.1.0+commit", true)]
    public async Task ComparesNumericVersionsAndNeverDowngrades(string latest, string current, bool expected)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(_ => Metadata(latest));
        using var service = new GitHubUpdateService(Repository, current, directory.Path, handler);
        Assert.Equal(expected, await service.CheckAsync(Token) is not null);
        Assert.False(Directory.Exists(directory.Path));
        Assert.All(handler.Requests, uri => Assert.Equal("api.github.com", uri.Host));
        Assert.False(handler.SawAuthorization);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("invalid-tag")]
    [InlineData("no-asset")]
    [InlineData("duplicate-asset")]
    [InlineData("no-digest")]
    [InlineData("invalid-digest")]
    [InlineData("wrong-host")]
    [InlineData("wrong-repository")]
    [InlineData("http")]
    [InlineData("null-assets")]
    [InlineData("null-tag")]
    [InlineData("null-digest")]
    [InlineData("oversized")]
    public async Task RejectsUnpublishedOrUnverifiableReleases(string kind)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(_ => Metadata("0.2.0", kind));
        using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
        await Assert.ThrowsAsync<AppUpdateException>(() => service.CheckAsync(Token));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HandlesNoPublicReleaseAndNetworkFailures(int status)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(_ => new((HttpStatusCode)status));
        using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
        await Assert.ThrowsAsync<AppUpdateException>(() => service.CheckAsync(Token));
    }

    [Fact]
    public async Task DownloadsOnlyCompleteVerifiedInstallerAndFollowsGitHubAssetRedirect()
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(uri => uri.Host switch
        {
            "api.github.com" => Metadata("0.2.0"),
            "github.com" => Redirect("https://release-assets.githubusercontent.com/asset"),
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) },
        });
        using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
        var update = Assert.IsType<AppUpdate>(await service.CheckAsync(Token));
        var result = await service.DownloadAsync(update, cancellationToken: Token);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(result.InstallerPath, Token));
        Assert.Equal(Digest, result.Sha256);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.download", SearchOption.AllDirectories));
        Assert.False(handler.SawAuthorization);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("too-long")]
    [InlineData("redirect-http")]
    [InlineData("redirect-host")]
    [InlineData("redirect-loop")]
    public async Task FailedDownloadsNeverLeaveAnExecutable(string kind)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(uri => uri.Host == "api.github.com" ? Metadata("0.2.0") : kind switch
        {
            "redirect-http" => Redirect("http://release-assets.githubusercontent.com/asset"),
            "redirect-host" => Redirect("https://untrusted.example/asset"),
            "redirect-loop" => Redirect(uri.AbsoluteUri),
            "truncated" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer[..^1]) },
            "too-long" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([.. Installer, 1]) },
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Installer.Length]) },
        });
        using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
        var update = Assert.IsType<AppUpdate>(await service.CheckAsync(Token));
        await Assert.ThrowsAsync<AppUpdateException>(() => service.DownloadAsync(update, cancellationToken: Token));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "untrusted.example" || uri.Scheme == "http");
    }

    [Fact]
    public async Task CancellationDeletesPartialFile()
    {
        using var directory = new TestDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var handler = new Handler(uri =>
        {
            if (uri.Host == "api.github.com") return Metadata("0.2.0");
            cancellation.Cancel();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
        });
        using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
        var update = Assert.IsType<AppUpdate>(await service.CheckAsync(Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(update, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/example/Mambo")]
    [InlineData("../Mambo")]
    [InlineData("example/Mambo/extra")]
    public async Task UnconfiguredBuildDoesNotMakeNetworkRequests(string repository)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler(_ => throw new InvalidOperationException("must not request"));
        using var service = new GitHubUpdateService(repository, "0.1.0", directory.Path, handler);
        Assert.False(service.IsConfigured);
        await Assert.ThrowsAsync<AppUpdateException>(() => service.CheckAsync(Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OversizedOrMalformedMetadataAndRawNetworkErrorsAreSafe()
    {
        using var directory = new TestDirectory();
        foreach (var text in new[] { "not json", new string(' ', 2 * 1024 * 1024 + 1) })
        {
            using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(text) });
            using var service = new GitHubUpdateService(Repository, "0.1.0", directory.Path, handler);
            await Assert.ThrowsAsync<AppUpdateException>(() => service.CheckAsync(Token));
        }
        using var network = new Handler(_ => throw new HttpRequestException("private signed URL"));
        using var failing = new GitHubUpdateService(Repository, "0.1.0", directory.Path, network);
        var error = await Assert.ThrowsAsync<AppUpdateException>(() => failing.CheckAsync(Token));
        Assert.DoesNotContain("signed", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    private static HttpResponseMessage Metadata(string version, string kind = "")
    {
        var tag = kind == "invalid-tag" ? "vnot-a-version" : "v" + version;
        var url = $"https://github.com/{Repository}/releases/download/{tag}/Mambo-{version}-win-x64-setup.exe";
        if (kind == "wrong-host") url = url.Replace("github.com", "untrusted.example", StringComparison.Ordinal);
        if (kind == "wrong-repository") url = url.Replace(Repository, "other/Mambo", StringComparison.Ordinal);
        if (kind == "http") url = url.Replace("https:", "http:", StringComparison.Ordinal);
        var digest = kind == "no-digest" ? "" : kind == "invalid-digest" ? "sha256:bad" : "sha256:" + Digest;
        var asset = $$$"""{"name":"Mambo-{{{version}}}-win-x64-setup.exe","state":"uploaded","size":{{{(kind == "oversized" ? 1024L * 1024 * 1024 : Installer.Length)}}},"digest":{{{(kind == "null-digest" ? "null" : '"' + digest + '"')}}},"browser_download_url":"{{{url}}}"}""";
        var assets = kind == "null-assets" ? "null" : kind == "no-asset" ? "[]" : kind == "duplicate-asset" ? $"[{asset},{asset}]" : $"[{asset}]";
        var json = $$$"""{"tag_name":{{{(kind == "null-tag" ? "null" : '"' + tag + '"')}}},"draft":{{{(kind == "draft" ? "true" : "false")}}},"prerelease":{{{(kind == "prerelease" ? "true" : "false")}}},"assets":{{{assets}}}}""";
        return new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new(url);
        return response;
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool SawAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SawAuthorization |= request.Headers.Authorization is not null;
            Requests.Add(request.RequestUri!);
            return Task.FromResult(response(request.RequestUri!));
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mambo-update-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
