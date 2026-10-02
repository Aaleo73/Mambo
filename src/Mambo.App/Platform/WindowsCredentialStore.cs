using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Mambo.Core.Session;

namespace Mambo.App.Platform;

public sealed unsafe partial class WindowsCredentialStore : ISecretStore
{
    public const string TargetName = "Mambo:emby-session:v1";
    public Task<SessionSecret?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CredReadW(TargetName, 1, 0, out var pointer) == 0)
        {
            if (Marshal.GetLastPInvokeError() == 1168) return Task.FromResult<SessionSecret?>(null);
            throw Failed();
        }
        try
        {
            var credential = (Credential*)pointer;
            if (credential->BlobSize is 0 or > 2560) throw Failed();
            try
            {
                var value = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(credential->Blob, (int)credential->BlobSize), StorageJsonContext.Default.SessionSecret);
                if (value is null || string.IsNullOrWhiteSpace(value.AccessToken) || string.IsNullOrWhiteSpace(value.UserId) || string.IsNullOrWhiteSpace(value.ServerId)) throw Failed();
                return Task.FromResult<SessionSecret?>(value);
            }
            catch (JsonException) { throw Failed(); }
        }
        finally { CredFree(pointer); }
    }
    public Task WriteAsync(SessionSecret secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(secret, StorageJsonContext.Default.SessionSecret);
        try
        {
            if (bytes.Length > 2560) throw new AppException(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "服务器认证信息过长，无法保存到 Windows 凭据管理器。", false));
            fixed (char* target = TargetName)
            fixed (char* user = "Mambo")
            fixed (byte* blob = bytes)
            {
                var credential = new Credential { Type = 1, TargetName = target, UserName = user, BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2 };
                if (CredWriteW(&credential, 0) == 0) throw Failed();
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CredDeleteW(TargetName, 1, 0) == 0 && Marshal.GetLastPInvokeError() != 1168) throw Failed();
        return Task.CompletedTask;
    }
    private static AppException Failed() => new(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "无法访问 Windows 凭据管理器，请检查当前用户权限。", true));
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags, Type;
        public char* TargetName;
        public nint Comment;
        public ulong LastWritten;
        public uint BlobSize;
        public byte* Blob;
        public uint Persist, AttributeCount;
        public nint Attributes, TargetAlias;
        public char* UserName;
    }
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CredReadW(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CredWriteW(Credential* credential, uint flags);
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CredDeleteW(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(nint buffer);
}
