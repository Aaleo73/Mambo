using Mambo.Core.Contracts;

namespace Mambo.Core.Persistence;

public static class AtomicFile
{
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> data, bool backup = false, CancellationToken cancellationToken = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(true);
            }
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Windows 的 Move(overwrite) 即使旧读者允许 Delete 仍可能拒绝替换；已有文件用 Replace。
                    if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null, true);
                    else File.Move(temporary, path);
                    break;
                }
                // ReplaceFile 的 1175 保留原件与临时文件的名称，可在短暂删除占用后重试；1176/1177 不具备此前提。
                catch (IOException error) when (attempt < 3 && OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33 or 1175)
                { await Task.Delay(TimeSpan.FromMilliseconds(25 << attempt), cancellationToken).ConfigureAwait(false); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new AppException(new(AppErrorKind.Persistence, ErrorCodes.PersistenceFailed, "无法保存本地数据，请检查磁盘空间与权限。", true,
            DiagnosticId: exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* 不用临时文件清理失败覆盖原始结果。 */ }
        }
    }
    public static byte[]? Read(string path, int maxBytes = 20 * 1024 * 1024)
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > maxBytes) throw new IOException("本地文件超出大小限制。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
