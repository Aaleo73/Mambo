using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Networking;
using Mambo.Core.Session;

namespace Mambo.Core.Images;

/// <summary>Account-scoped image downloads with six priority slots and per-waiter cancellation.</summary>
public sealed class ImageFetcher : IImageService, IDisposable, IAsyncDisposable
{
    private static readonly int[] Widths = [160, 240, 320, 480, 640, 960, 1280, 1920, 2560];
    private readonly AccountContext accounts;
    private readonly ImageByteCache cache;
    private readonly Guid deviceId;
    private readonly TimeProvider clock;
    private readonly HttpClient client;
    private readonly string version;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Ticket, (int Priority, long Sequence)> queue = new();
    private int running;
    private long sequence;
    private bool disposed;
    private Task drain = Task.CompletedTask;
    public event Action<AccountSession>? AuthenticationExpired;

    public ImageFetcher(AccountContext accounts, ImageByteCache cache, Guid deviceId, TimeProvider? timeProvider = null,
        HttpMessageHandler? handler = null, string version = "0.1.0")
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(cache);
        this.accounts = accounts;
        this.cache = cache;
        this.deviceId = deviceId;
        this.version = version;
        clock = timeProvider ?? TimeProvider.System;
        client = new HttpClient(handler ?? EmbyApi.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
    }

    public static int RoundPixelWidth(int pixelWidth)
    {
        if (pixelWidth <= 0) throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "图片宽度必须大于零。", false));
        return Widths.FirstOrDefault(width => width >= pixelWidth, Widths[^1]);
    }

    public async Task<ReadOnlyMemory<byte>> FetchAsync(ImageRef image, int pixelWidth,
        ImagePriority priority = ImagePriority.Visible, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(image.ItemId) || image.ItemId is "." or ".." || image.ItemId.Any(char.IsControl) || image.Index < 0 ||
            Encoding.UTF8.GetByteCount(image.ItemId) > 256 ||
            !Enum.IsDefined(image.Kind) || !Enum.IsDefined(priority))
            throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "图片请求参数无效。", false));
        var width = RoundPixelWidth(pixelWidth);
        cancellationToken.ThrowIfCancellationRequested();
        var account = accounts.Current ?? throw new AppException(new(AppErrorKind.Auth, ErrorCodes.NotLoggedIn, "请先连接服务器。", false));
        CancellationToken accountToken;
        try { accountToken = account.Token; }
        catch (ObjectDisposedException) { throw new AppException(new(AppErrorKind.Cancelled, ErrorCodes.SessionChanged, "账号已切换，请重新加载图片。", false)); }
        var key = new ImageRequestKey(account.Address.Uri.AbsoluteUri, image.ItemId, image.Kind, image.Tag, width, Index: image.Index);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accountToken, shutdown.Token);
        var cached = await cache.TryGetAsync(key, scope.Token).ConfigureAwait(false);
        if (cached is not null) return cached.Value;
        Flight flight;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var identity = account.Scope + ":" + key.Hash;
            if (!flights.TryGetValue(identity, out flight!))
            {
                flight = new Flight(identity, key, account, clock, priority, cache.Generation, accountToken, shutdown.Token);
                flights.Add(identity, flight);
                queue.Enqueue(new Ticket(flight, flight.QueueVersion), ((int)priority, sequence++));
            }
            else if (!flight.Running && (int)priority < (int)flight.Priority)
            {
                flight.Priority = priority;
                flight.QueueVersion++;
                queue.Enqueue(new Ticket(flight, flight.QueueVersion), ((int)priority, sequence++));
            }
            flight.Waiters++;
            Pump();
        }
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, flight.Token);
            return await flight.Completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !accountToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
        { throw new AppException(ErrorText.Network("加载图片")); }
        finally { ReleaseWaiter(flight); }
    }

    private void Pump()
    {
        while (running < 6 && queue.TryDequeue(out var ticket, out _))
        {
            var flight = ticket.Flight;
            if (flight.Running || flight.Completion.Task.IsCompleted || ticket.Version != flight.QueueVersion) continue;
            if (flight.Token.IsCancellationRequested)
            {
                flights.Remove(flight.Identity);
                flight.Completion.TrySetCanceled(flight.Token);
                flight.Dispose();
                continue;
            }
            flight.Running = true;
            running++;
            _ = DownloadAsync(flight);
        }
    }

    private async Task DownloadAsync(Flight flight)
    {
        try
        {
            var bytes = await DownloadBytesAsync(flight.Account, flight.Key, flight.Token).ConfigureAwait(false);
            await cache.StoreAsync(flight.Key, bytes, flight.CacheGeneration, flight.Token).ConfigureAwait(false);
            flight.Token.ThrowIfCancellationRequested();
            flight.Completion.TrySetResult(bytes);
        }
        catch (OperationCanceledException) { flight.Completion.TrySetCanceled(flight.Token); }
        catch (AppException error) { flight.Completion.TrySetException(error); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { flight.Completion.TrySetException(new AppException(ErrorText.Network("加载图片"))); }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
        { flight.Completion.TrySetCanceled(flight.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { flight.Completion.TrySetException(new AppException(ErrorText.InvalidResponse("加载图片"))); }
        finally
        {
            lock (gate)
            {
                flights.Remove(flight.Identity);
                running--;
                flight.Dispose();
                if (!disposed) Pump();
            }
        }
    }

    private async Task<byte[]> DownloadBytesAsync(AccountSession account, ImageRequestKey key, CancellationToken cancellationToken)
    {
        var kind = key.Type.ToString();
        var path = "Items/" + EmbyApi.Escape(key.ItemId) + "/Images/" + kind;
        path += "?";
        if (key.Tag is not null) path += "Tag=" + EmbyApi.Escape(key.Tag) + "&";
        path += string.Create(CultureInfo.InvariantCulture,
            $"MaxWidth={key.MaxWidth}&MaxHeight={key.MaxHeight}&Index={key.Index}&Quality={key.Quality}");
        var initial = account.Address.Endpoint(path);
        var uri = initial;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            AuthHeader.Apply(request, deviceId, version, account);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (hop >= 5 || location is null || !Uri.TryCreate(uri, location, out var target) ||
                    target.UserInfo.Length != 0 || target.Scheme is not ("http" or "https") ||
                    !target.DnsSafeHost.Equals(initial.DnsSafeHost, StringComparison.OrdinalIgnoreCase) || target.Port != initial.Port ||
                    target.Scheme != uri.Scheme && !(uri.Scheme == "http" && target.Scheme == "https"))
                    throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidResponse, "图片重定向地址无效，已停止下载。", false));
                uri = StripAuthentication(target);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new AppException(new(AppErrorKind.Server, ErrorCodes.ImageNotFound, "这张图片暂时不可用。", false, "加载图片", 404));
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    AuthenticationExpired?.Invoke(account);
                throw new AppException(ErrorText.Http((int)response.StatusCode, "加载图片"));
            }
            if (response.Content.Headers.ContentLength > cache.MaxEntryBytes)
                throw new AppException(ErrorText.InvalidResponse("加载图片"));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await EmbyApi.ReadBoundedAsync(stream, cache.MaxEntryBytes, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) throw new AppException(ErrorText.InvalidResponse("加载图片"));
            return bytes;
        }
    }

    private static Uri StripAuthentication(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part =>
        {
            var name = Uri.UnescapeDataString(part.Split('=', 2)[0]);
            return !name.Equals("api_key", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("access_token", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("token", StringComparison.OrdinalIgnoreCase) && !name.Equals("x-emby-token", StringComparison.OrdinalIgnoreCase);
        });
        return new UriBuilder(uri) { Query = string.Join('&', query), Fragment = "" }.Uri;
    }

    private void ReleaseWaiter(Flight flight)
    {
        lock (gate)
        {
            flight.Waiters--;
            if (flight.Waiters != 0 || flight.Completion.Task.IsCompleted) return;
            flight.Cancel();
            if (!flight.Running)
            {
                flights.Remove(flight.Identity);
                flight.Completion.TrySetCanceled(flight.Token);
                flight.Dispose();
                if (!disposed) Pump();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            drain = Task.WhenAll(flights.Values.Select(flight => flight.Completion.Task));
            shutdown.Cancel();
            foreach (var flight in flights.Values.Where(flight => !flight.Running).ToArray())
            {
                flight.Completion.TrySetCanceled(flight.Token);
                flight.Dispose();
                flights.Remove(flight.Identity);
            }
            queue.Clear();
        }
        client.Dispose();
        AuthenticationExpired = null;
        shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task pending;
        lock (gate) pending = drain;
        try { await pending.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or AppException) { }
    }

    private sealed record Ticket(Flight Flight, int Version);
    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource deadline;
        private readonly CancellationTokenSource cancellation;
        public Flight(string identity, ImageRequestKey key, AccountSession account, TimeProvider clock, ImagePriority priority,
            int cacheGeneration, CancellationToken accountToken, CancellationToken shutdownToken)
        {
            Identity = identity; Key = key; Account = account; Priority = priority; CacheGeneration = cacheGeneration;
            deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(accountToken, shutdownToken, deadline.Token);
            Token = cancellation.Token;
        }
        public string Identity { get; }
        public ImageRequestKey Key { get; }
        public AccountSession Account { get; }
        public ImagePriority Priority { get; set; }
        public int CacheGeneration { get; }
        public int Waiters { get; set; }
        public bool Running { get; set; }
        public int QueueVersion { get; set; }
        public CancellationToken Token { get; }
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel() => cancellation.Cancel();
        public void Dispose() { cancellation.Dispose(); deadline.Dispose(); }
    }
}
