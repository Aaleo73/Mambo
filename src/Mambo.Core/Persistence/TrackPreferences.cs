using System.Text;
using Mambo.Core.Contracts;
using Mambo.Core.Playback;
using Mambo.Core.Session;

namespace Mambo.Core.Persistence;

public sealed record TrackPreferenceEpoch(string AudioLanguage, string SubtitleLanguage, long AudioRevision, long SubtitleRevision);
public sealed record TrackChoice(bool Disabled, TrackFingerprint? Fingerprint = null, string? LocalSubtitleId = null);
public sealed record TrackPreferenceRecord(TrackChoice? Audio = null, TrackChoice? Subtitle = null);

/// <summary>只接收成功手选；账号和内容键由后端构造，设置修订在同一写锁内核验。</summary>
public sealed class TrackPreferences(SettingsStore settings, AccountContext accounts)
{
    public TrackPreferenceEpoch Capture() => settings.CaptureTrackEpoch();

    public TrackChoice? Get(AccountSession account, PlaybackEntry entry, TrackKind kind, TrackPreferenceEpoch epoch, bool exactItem = false)
    {
        RequireCurrent(account);
        RequireKind(kind);
        return settings.TrackPreference(Key(account, entry, exactItem), kind, epoch);
    }

    public async Task SaveAsync(AccountSession account, PlaybackEntry entry, TrackKind kind, TrackChoice choice,
        TrackPreferenceEpoch epoch, bool exactItem, CancellationToken token)
    {
        RequireCurrent(account);
        RequireKind(kind);
        if (!ValidChoice(choice, kind, exactItem)) throw Invalid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        await settings.SaveTrackPreferenceAsync(Key(account, entry, exactItem), kind, choice, epoch, linked.Token).ConfigureAwait(false);
    }

    public async Task ClearExactSubtitleAsync(AccountSession account, PlaybackEntry entry, TrackPreferenceEpoch epoch, CancellationToken token)
    {
        RequireCurrent(account);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        await settings.SaveTrackPreferenceAsync(Key(account, entry, true), TrackKind.Subtitle, null, epoch, linked.Token).ConfigureAwait(false);
    }

    private void RequireCurrent(AccountSession account)
    {
        account.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(accounts.Current, account))
            throw new AppException(new(AppErrorKind.Auth, ErrorCodes.SessionChanged, "账号已改变，请重新播放。", false));
    }

    private static void RequireKind(TrackKind kind) { if (!Enum.IsDefined(kind)) throw Invalid(); }
    private static string Key(AccountSession account, PlaybackEntry entry, bool exactItem)
    {
        var series = !exactItem && !string.IsNullOrWhiteSpace(entry.SeriesId);
        var id = series ? entry.SeriesId! : entry.ItemId;
        if (!ValidId(id)) throw Invalid();
        return account.Scope + (series ? "|series|" : "|item|") + id;
    }

    internal static bool IsValidKey(string key) => key.Length >= 71 && key.Take(64).All(char.IsAsciiHexDigit) &&
        (key.AsSpan(64).StartsWith("|series|", StringComparison.Ordinal) ? ValidId(key[72..]) :
        key.AsSpan(64).StartsWith("|item|", StringComparison.Ordinal) && ValidId(key[70..]));

    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id is not "." and not ".." &&
        Encoding.UTF8.GetByteCount(id) <= 256 && !id.Any(char.IsControl) && id.IndexOfAny(['/', '\\', ':', '?', '|']) < 0;

    internal static bool ValidChoice(TrackChoice? choice, TrackKind kind, bool exactItem)
    {
        if (choice is null) return false;
        if (choice.Disabled) return kind == TrackKind.Subtitle && choice.Fingerprint is null && choice.LocalSubtitleId is null;
        if (choice.LocalSubtitleId is { } id)
            return kind == TrackKind.Subtitle && exactItem && choice.Fingerprint is null && id.Length is > 0 and <= 128 &&
                id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
        if (choice.Fingerprint is not { } fingerprint || fingerprint.Kind != kind || !Enum.IsDefined(fingerprint.Kind)) return false;
        return fingerprint.Title == TrackSelection.SafeText(fingerprint.Title) && fingerprint.Language == TrackSelection.NormalizeLanguage(fingerprint.Language) &&
            fingerprint.Codec == TrackSelection.SafeText(fingerprint.Codec) && fingerprint.AudioChannels == TrackSelection.SafeText(fingerprint.AudioChannels);
    }

    private static AppException Invalid() => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "轨道选择无效。", false));
}
