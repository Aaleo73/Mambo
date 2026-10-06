using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;
using Microsoft.UI.Dispatching;

namespace Mambo.App.ViewModels;

/// <summary>设置页：服务器、播放、外观、关于四组。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PathSaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly ISessionService session;
    private readonly ISettingsService settings;
    private readonly IPlaybackService playback;
    private readonly Navigator navigator;
    private readonly ToastService toasts;
    private readonly DialogService dialogs;
    private readonly ThemeService theme;
    private readonly DispatcherQueueTimer pathSaveTimer;
    private readonly CancellationTokenSource lifetime = new();
    private long draftVersion;
    private long cacheReadVersion;
    private bool draftDirty;
    private bool validatingMpv;
    private bool applying;
    private bool disposed;

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
        pathSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        pathSaveTimer.Interval = PathSaveDelay;
        pathSaveTimer.IsRepeating = false;
        pathSaveTimer.Tick += (_, _) => _ = SaveMpvPathAsync();
        try
        {
            var defaults = settings.ConnectionDefaults;
            ServerAddress = defaults.ServerAddress;
            LoginUserName = defaults.UserName;
            applying = true;
            MpvPath = settings.Current.ExternalMpvPath ?? "";
            applying = false;
            session.Changed += OnSessionChanged;
            settings.Changed += OnSettingsChanged;
            theme.Changed += OnThemeChanged;
            ApplySession();
            ApplySettings();
            ApplyTheme();
            _ = RefreshCacheSizeAsync();
        }
        catch
        {
            FailedConstruction.Release(Dispose);
            throw;
        }
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
    [NotifyPropertyChangedFor(nameof(CanValidateMpv))]
    public partial string MpvPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanValidateMpv), nameof(CanEditMpvPath), nameof(ValidateMpvText))]
    public partial bool IsValidatingMpv { get; private set; }

    public bool CanValidateMpv => !IsValidatingMpv && MpvPath.Trim().Length > 0;
    public bool CanEditMpvPath => !IsValidatingMpv;
    public string ValidateMpvText => IsValidatingMpv ? "验证中…" : "验证并启用";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMpvError))]
    public partial string MpvError { get; private set; } = "";

    public bool HasMpvError => MpvError.Length > 0;

    [ObservableProperty]
    public partial HdrMode Hdr { get; private set; }

    public string HdrLabel => Hdr switch { HdrMode.Always => "始终 HDR", HdrMode.Off => "关闭", _ => "自动" };
    public bool IsHdrAuto => Hdr == HdrMode.Auto;
    public bool IsHdrAlways => Hdr == HdrMode.Always;
    public bool IsHdrOff => Hdr == HdrMode.Off;

    [ObservableProperty]
    public partial bool HardwareDecoding { get; private set; }

    [ObservableProperty]
    public partial bool IsThemeSystem { get; private set; }

    [ObservableProperty]
    public partial bool IsThemeLight { get; private set; }

    [ObservableProperty]
    public partial bool IsThemeDark { get; private set; }

    [ObservableProperty]
    public partial string CacheSizeText { get; private set; } = "计算中…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearCache), nameof(ClearCacheText))]
    public partial bool IsClearingCache { get; private set; }

    public bool CanClearCache => !IsClearingCache;
    public string ClearCacheText => IsClearingCache ? "清除中…" : "清除缓存";
    public string LogDirectory => settings.LogDirectory;
    public bool CanOpenLogs => !string.IsNullOrWhiteSpace(LogDirectory);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pathSaveTimer.Stop();
        lifetime.Cancel();
        session.Changed -= OnSessionChanged;
        settings.Changed -= OnSettingsChanged;
        theme.Changed -= OnThemeChanged;
        lifetime.Dispose();
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

    public Task SetPlaybackModeAsync(PlaybackMode mode) =>
        mode == PlaybackMode.External && !CanUseExternal ? Task.CompletedTask
            : UpdateAsync(s => s with { PlaybackMode = mode });
    public Task SetHdrAsync(HdrMode mode) => UpdateAsync(s => s with { HdrMode = mode });

    public Task SetHardwareDecodingAsync(bool enabled) =>
        enabled == HardwareDecoding ? Task.CompletedTask
            : UpdateAsync(s => s with { HardwareDecoding = enabled ? HardwareDecodingMode.Auto : HardwareDecodingMode.Off });

    public Task SetThemeAsync(ThemeMode mode) => UpdateAsync(s => s with
    {
        ThemeMode = mode switch
        {
            ThemeMode.Light => SettingsThemeMode.Light,
            ThemeMode.Dark => SettingsThemeMode.Dark,
            _ => SettingsThemeMode.System,
        },
    });

    public async Task ClearCacheAsync()
    {
        if (disposed || IsClearingCache ||
            !await dialogs.ConfirmAsync(new ConfirmRequest("清除缓存？", "登录信息和设置不受影响。", "清除"))) return;
        IsClearingCache = true;
        try
        {
            await settings.ClearCacheAsync(lifetime.Token);
            Images.ImageLoader.Current?.ClearDecodedCache();
            await RefreshCacheSizeAsync();
            toasts.Show(ToastKind.Success, "已清除缓存");
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            toasts.Show(ToastKind.Error, "无法清除缓存，请稍后重试");
        }
        finally
        {
            if (!disposed) IsClearingCache = false;
        }
    }

    public async Task RefreshCacheSizeAsync()
    {
        if (disposed) return;
        var version = ++cacheReadVersion;
        try
        {
            var bytes = await settings.GetCacheSizeAsync(lifetime.Token);
            if (!disposed && version == cacheReadVersion) CacheSizeText = FormatBytes(bytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is AppException or IOException or UnauthorizedAccessException)
        {
            if (!disposed && version == cacheReadVersion) CacheSizeText = "暂时无法读取";
        }
    }

    partial void OnMpvPathChanged(string value)
    {
        if (applying) return;
        ++draftVersion;
        draftDirty = true;
        pathSaveTimer.Stop();
        pathSaveTimer.Start();
        ApplyMpvStatus();
    }

    public async Task ChooseMpvAsync(string path)
    {
        if (disposed || IsValidatingMpv || string.IsNullOrWhiteSpace(path)) return;
        MpvPath = path;
        await ValidateMpvAsync();
    }

    /// <summary>只由选择文件、验证按钮或 Enter 显式调用，不在读取设置或编辑防抖时执行。</summary>
    public async Task ValidateMpvAsync()
    {
        if (disposed || !CanValidateMpv) return;
        pathSaveTimer.Stop();
        var path = MpvPath.Trim();
        var version = draftVersion;
        validatingMpv = true;
        ApplyMpvStatus();
        try
        {
            await settings.UpdateAsync(s => s with { ExternalMpvPath = path }, lifetime.Token);
            if (version != draftVersion || disposed) return;
            await settings.ValidateExternalPlayerAsync(path, lifetime.Token);
            if (version != draftVersion || disposed) return;
            draftDirty = false;
            SetMpvDraft(settings.Current.ExternalMpvPath ?? "");
            if (settings.ExternalPlayerStatus == ExternalPlayerStatus.Approved)
                await settings.UpdateAsync(s => s with { PlaybackMode = PlaybackMode.External }, lifetime.Token);
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            validatingMpv = false;
            if (!disposed) ApplySettings();
        }
    }

    private async Task SaveMpvPathAsync()
    {
        if (disposed) return;
        var path = MpvPath.Trim();
        var version = draftVersion;
        try
        {
            await settings.UpdateAsync(s => s with { ExternalMpvPath = path.Length == 0 ? null : path }, lifetime.Token);
            if (disposed || version != draftVersion) return;
            draftDirty = false;
            ApplySettings();
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
        catch (OperationCanceledException) { }
    }

    private async Task UpdateAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            if (disposed) return;
            await settings.UpdateAsync(update, lifetime.Token);
        }
        catch (AppException ex)
        {
            toasts.Show(ToastKind.Error, ex.Error.Message);
        }
        catch (OperationCanceledException) { }
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
        var persistedPath = current.ExternalMpvPath ?? "";
        // 无关的音量或主题变更不会覆盖尚未保存的输入。
        if (!draftDirty) SetMpvDraft(persistedPath);
        applying = true;
        try
        {
            IsEmbedded = current.PlaybackMode == PlaybackMode.Embedded;
            IsExternal = current.PlaybackMode == PlaybackMode.External;
            Hdr = current.HdrMode;
            HardwareDecoding = current.HardwareDecoding == HardwareDecodingMode.Auto;
            ApplyMpvStatus();
            OnPropertyChanged(nameof(HdrLabel));
            OnPropertyChanged(nameof(IsHdrAuto));
            OnPropertyChanged(nameof(IsHdrAlways));
            OnPropertyChanged(nameof(IsHdrOff));
            OnPropertyChanged(nameof(LogDirectory));
            OnPropertyChanged(nameof(CanOpenLogs));
        }
        finally
        {
            applying = false;
        }
    }

    private void SetMpvDraft(string path)
    {
        var wasApplying = applying;
        applying = true;
        MpvPath = path;
        applying = wasApplying;
    }

    private void ApplyMpvStatus()
    {
        var status = settings.ExternalPlayerStatus;
        IsValidatingMpv = validatingMpv || status == ExternalPlayerStatus.Validating;
        var draftMatches = string.Equals(MpvPath.Trim(), settings.Current.ExternalMpvPath ?? "", StringComparison.OrdinalIgnoreCase);
        CanUseExternal = status == ExternalPlayerStatus.Approved && draftMatches && !IsValidatingMpv;
        // 只在需要用户处理时出文字：批准过的文件变了。验证中写在按钮上，其余状态看分段按钮就够。
        var pendingDraft = !draftMatches && MpvPath.Trim().Length > 0;
        MpvError = !IsValidatingMpv && !pendingDraft && status == ExternalPlayerStatus.Invalid ? "需要重新验证" : "";
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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{Math.Max(bytes, 0)} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
