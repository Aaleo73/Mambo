using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;
using Microsoft.UI.Dispatching;

namespace Mambo.App.ViewModels;

public enum StatusTone
{
    None,
    Info,
    Ok,
    Warning,
}

/// <summary>设置页：服务器、播放、外观、关于四组。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(400);
    private readonly ISessionService session;
    private readonly ISettingsService settings;
    private readonly IPlaybackService playback;
    private readonly Navigator navigator;
    private readonly ToastService toasts;
    private readonly DialogService dialogs;
    private readonly ThemeService theme;
    private readonly DispatcherQueueTimer validationTimer;
    private bool applying;

    public SettingsViewModel(ISessionService session, ISettingsService settings, IPlaybackService playback, Navigator navigator,
        ToastService toasts, DialogService dialogs, ThemeService theme)
    {
        this.session = session;
        this.settings = settings;
        this.playback = playback;
        this.navigator = navigator;
        this.toasts = toasts;
        this.dialogs = dialogs;
        this.theme = theme;
        validationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        validationTimer.Interval = ValidationDelay;
        validationTimer.IsRepeating = false;
        validationTimer.Tick += (_, _) => _ = ValidateMpvAsync();
        var defaults = settings.ConnectionDefaults;
        ServerAddress = defaults.ServerAddress;
        LoginUserName = defaults.UserName;
        MpvPath = settings.Current.ExternalMpvPath ?? "";
        session.Changed += OnSessionChanged;
        settings.Changed += OnSettingsChanged;
        theme.Changed += OnThemeChanged;
        ApplySession();
        ApplySettings();
        ApplyTheme();
    }

    public static string Version { get; } = ReadVersion();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedOut))]
    public partial bool IsLoggedIn { get; private set; }

    public bool IsLoggedOut => !IsLoggedIn;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind 绑定实例属性。")]
    public string AppVersion => Version;

    [ObservableProperty]
    public partial string UserName { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectedServer))]
    public partial string ConnectedServer { get; private set; } = "";

    public bool HasConnectedServer => ConnectedServer.Length > 0;

    public string AvatarText => string.IsNullOrEmpty(UserName) ? "" : UserName[..1].ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial string LoginUserName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectText))]
    public partial bool IsConnecting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisconnectText), nameof(CanDisconnect))]
    public partial bool IsDisconnecting { get; private set; }

    public bool CanDisconnect => !IsDisconnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginError))]
    public partial string LoginError { get; private set; } = "";

    public bool HasLoginError => LoginError.Length > 0;
    public bool CanConnect => !IsConnecting && ServerAddress.Trim().Length > 0 && LoginUserName.Trim().Length > 0;
    public string ConnectText => IsConnecting ? "连接中…" : "连接";
    public string DisconnectText => IsDisconnecting ? "断开中…" : "断开连接";

    [ObservableProperty]
    public partial bool IsEmbedded { get; private set; }

    [ObservableProperty]
    public partial bool IsExternal { get; private set; }

    [ObservableProperty]
    public partial bool CanUseExternal { get; private set; }

    [ObservableProperty]
    public partial string MpvPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMpvStatus), nameof(IsMpvOk), nameof(IsMpvWarning), nameof(IsMpvInfo))]
    public partial StatusTone MpvStatusTone { get; private set; }

    [ObservableProperty]
    public partial string MpvStatusText { get; private set; } = "";

    public bool HasMpvStatus => MpvStatusTone != StatusTone.None;
    public bool IsMpvOk => MpvStatusTone == StatusTone.Ok;
    public bool IsMpvWarning => MpvStatusTone == StatusTone.Warning;
    public bool IsMpvInfo => MpvStatusTone == StatusTone.Info;

    [ObservableProperty]
    public partial HdrMode Hdr { get; private set; }

    public string HdrLabel => Hdr switch { HdrMode.Always => "始终 HDR", HdrMode.Off => "关闭", _ => "自动" };
    public bool IsHdrAuto => Hdr == HdrMode.Auto;
    public bool IsHdrAlways => Hdr == HdrMode.Always;
    public bool IsHdrOff => Hdr == HdrMode.Off;

    [ObservableProperty]
    public partial bool HardwareDecoding { get; private set; }

    public string HardwareDecodingText => HardwareDecoding ? "开" : "关";

    [ObservableProperty]
    public partial bool IsThemeSystem { get; private set; }

    [ObservableProperty]
    public partial bool IsThemeLight { get; private set; }

    [ObservableProperty]
    public partial bool IsThemeDark { get; private set; }

    public void Dispose()
    {
        validationTimer.Stop();
        session.Changed -= OnSessionChanged;
        settings.Changed -= OnSettingsChanged;
        theme.Changed -= OnThemeChanged;
    }

    public async Task ConnectAsync(string password)
    {
        if (!CanConnect) return;
        IsConnecting = true;
        LoginError = "";
        var address = ServerAddress.Trim();
        var user = LoginUserName.Trim();
        try
        {
            await session.LoginAsync(new LoginRequest(address, user, password ?? ""));
            await settings.SaveConnectionDefaultsAsync(new ConnectionDefaults(address, user));
            navigator.Navigate(Route.Home);
        }
        catch (AppException ex)
        {
            LoginError = ex.Error.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsDisconnecting) return;
        if (playback.Current is not null &&
            !await dialogs.ConfirmAsync(new ConfirmRequest("注销并结束播放？", "当前播放将结束并保存进度。", "注销", danger: true)))
            return;
        IsDisconnecting = true;
        try
        {
            var result = await session.LogoutAsync();
            if (result.RemoteLogoutFailed) toasts.Show(ToastKind.Warning, "本地已断开，但服务器会话可能仍有效");
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
        finally
        {
            IsDisconnecting = false;
        }
    }

    public Task SetPlaybackModeAsync(PlaybackMode mode) => UpdateAsync(s => s with { PlaybackMode = mode });
    public Task SetHdrAsync(HdrMode mode) => UpdateAsync(s => s with { HdrMode = mode });

    public Task SetHardwareDecodingAsync(bool enabled) =>
        enabled == HardwareDecoding ? Task.CompletedTask
            : UpdateAsync(s => s with { HardwareDecoding = enabled ? HardwareDecodingMode.Auto : HardwareDecodingMode.Off });

    public void SetTheme(ThemeMode mode) => theme.Set(mode);

    public async Task ClearCacheAsync()
    {
        if (!await dialogs.ConfirmAsync(new ConfirmRequest("清除缓存？", "登录信息和设置不受影响。", "清除"))) return;
        try
        {
            await settings.ClearCacheAsync();
            toasts.Show(ToastKind.Success, "已清除缓存");
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
    }

    partial void OnMpvPathChanged(string value)
    {
        if (applying) return;
        validationTimer.Stop();
        validationTimer.Start();
    }

    private async Task ValidateMpvAsync()
    {
        var path = MpvPath.Trim();
        try
        {
            await settings.UpdateAsync(s => s with { ExternalMpvPath = path.Length == 0 ? null : path });
            if (path.Length > 0) await settings.ValidateExternalPlayerAsync(path);
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
    }

    private async Task UpdateAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            await settings.UpdateAsync(update);
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => ApplySession();
    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();
    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplySession()
    {
        var current = session.Current;
        IsLoggedIn = current is not null && session.State is SessionState.LoggedIn or SessionState.Restoring or SessionState.Unreachable;
        UserName = current?.UserName ?? "";
        ConnectedServer = settings.ConnectionDefaults.ServerAddress;
        OnPropertyChanged(nameof(AvatarText));
    }

    private void ApplySettings()
    {
        var current = settings.Current;
        applying = true;
        try
        {
            IsEmbedded = current.PlaybackMode == PlaybackMode.Embedded;
            IsExternal = current.PlaybackMode == PlaybackMode.External;
            Hdr = current.HdrMode;
            HardwareDecoding = current.HardwareDecoding == HardwareDecodingMode.Auto;
            var status = settings.ExternalPlayerStatus;
            CanUseExternal = status == ExternalPlayerStatus.Approved;
            (MpvStatusTone, MpvStatusText) = status switch
            {
                ExternalPlayerStatus.Validating => (StatusTone.Info, "正在验证…"),
                ExternalPlayerStatus.Approved => (StatusTone.Ok, "已验证"),
                ExternalPlayerStatus.Invalid when MpvPath.Trim().Length > 0 => (StatusTone.Warning, "未找到 mpv.exe"),
                _ => (StatusTone.None, ""),
            };
            OnPropertyChanged(nameof(HdrLabel));
            OnPropertyChanged(nameof(IsHdrAuto));
            OnPropertyChanged(nameof(IsHdrAlways));
            OnPropertyChanged(nameof(IsHdrOff));
            OnPropertyChanged(nameof(HardwareDecodingText));
        }
        finally
        {
            applying = false;
        }
    }

    private void ApplyTheme()
    {
        IsThemeSystem = theme.Mode == ThemeMode.System;
        IsThemeLight = theme.Mode == ThemeMode.Light;
        IsThemeDark = theme.Mode == ThemeMode.Dark;
    }

    private static string ReadVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return "v" + text;
    }
}
