using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Persistence;

/// <summary>后端按账号和整剧/影片保存画质，前端不构造持久化键。</summary>
public sealed class VideoQualityPreferences(SettingsStore settings, AccountContext accounts)
{
    public VideoQualityMode Get(AccountSession account, PlaybackEntry entry)
    {
        RequireCurrent(account);
        return settings.VideoQualityPreference(Key(account, entry)) ?? VideoQualityMode.Standard;
    }

    public async Task SaveAsync(AccountSession account, PlaybackEntry entry, VideoQualityMode mode, CancellationToken cancellationToken)
    {
        RequireCurrent(account);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Token);
        await settings.SaveVideoQualityPreferenceAsync(Key(account, entry), mode, linked.Token).ConfigureAwait(false);
    }

    private void RequireCurrent(AccountSession account)
    {
        account.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(accounts.Current, account))
            throw new AppException(new(AppErrorKind.Auth, ErrorCodes.SessionChanged, "账号已改变，请重新播放。", false));
    }

    private static string Key(AccountSession account, PlaybackEntry entry)
    {
        var series = !string.IsNullOrWhiteSpace(entry.SeriesId);
        var id = series ? entry.SeriesId! : entry.ItemId;
        if (!IsValidId(id)) throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "媒体标识无效。", false));
        return account.Scope + (series ? "|series|" : "|item|") + id;
    }

    internal static bool IsValidKey(string key)
    {
        if (key.Length < 71 || !key.Take(64).All(char.IsAsciiHexDigit)) return false;
        var suffix = key.AsSpan(64);
        return suffix.StartsWith("|series|", StringComparison.Ordinal) ? IsValidId(suffix[8..].ToString()) :
            suffix.StartsWith("|item|", StringComparison.Ordinal) && IsValidId(suffix[6..].ToString());
    }

    private static bool IsValidId(string id) => !string.IsNullOrWhiteSpace(id) && id is not "." and not ".." &&
        Encoding.UTF8.GetByteCount(id) <= 256 && !id.Any(char.IsControl);
}
