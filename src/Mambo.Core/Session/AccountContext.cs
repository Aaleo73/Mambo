using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Networking;

namespace Mambo.Core.Session;

public sealed record SessionSecret(string ServerAddress, string ServerId, string UserId, string UserName, string AccessToken)
{
    public override string ToString() => "SessionSecret { <redacted> }";
}

public interface ISecretStore
{
    Task<SessionSecret?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(SessionSecret secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountSession : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    public AccountSession(SessionSecret secret)
    {
        Secret = secret; Address = ServerAddress.Normalize(secret.ServerAddress);
        Scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Address.Uri.AbsoluteUri + "|" + secret.ServerId + "|" + secret.UserId)));
        Token = lifetime.Token;
    }
    public SessionSecret Secret { get; }
    public ServerAddress Address { get; }
    public string Scope { get; }
    public CancellationToken Token { get; }
    public override string ToString() => "AccountSession { <redacted> }";
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); lifetime.Dispose();
    }
}

public sealed class AccountContext : IDisposable
{
    private readonly object gate = new();
    private AccountSession? current;
    public AccountSession? Current { get { lock (gate) return current; } }
    public event Action? Changed;
    public void Set(AccountSession? value)
    {
        AccountSession? previous;
        lock (gate) { previous = current; if (ReferenceEquals(previous, value)) return; current = value; }
        previous?.Dispose();
        Changed?.Invoke();
    }
    public void Dispose() { Set(null); Changed = null; }
}
