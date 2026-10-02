namespace Mambo.Core.Contracts;

/// <summary>前端分支使用的稳定错误码，假实现和真实服务共享。</summary>
public static class ErrorCodes
{
    public const string ReplaceConfirmationRequired = "playback.replace_confirmation_required";
    public const string PlaybackBusy = "playback.busy";
    public const string SessionClosed = "playback.session_closed";
    public const string NotLoggedIn = "session.not_logged_in";
    public const string SessionExpired = "session.expired";
    public const string SessionChanged = "session.changed";
    public const string AlreadyLoggedIn = "session.already_logged_in";
    public const string ItemNotFound = "media.not_found";
    public const string ItemNotPlayable = "media.not_playable";
    public const string ImageNotFound = "image.not_found";
    public const string InvalidArgument = "contract.invalid_argument";
    public const string NetworkUnavailable = "network.unavailable";
    public const string InvalidResponse = "contract.invalid_response";
    public const string PersistenceFailed = "persistence.failed";
    public const string PlaybackFailed = "playback.failed";
}
