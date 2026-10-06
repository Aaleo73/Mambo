using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.App.Shell;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>由 UI 线程持有；检查失败不会打断启动或播放。</summary>
public sealed partial class AppUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IAppUpdateService service;
    private readonly ISettingsService settings;
    private readonly DialogService dialogs;
    private readonly ToastService toasts;
    private readonly Navigator navigator;
    private readonly WindowContext window;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? download;
    private AppUpdate? available;
    private PreparedAppUpdate? prepared;
    private bool disposed;

    public AppUpdateViewModel(IAppUpdateService service, ISettingsService settings, DialogService dialogs, ToastService toasts, Navigator navigator, WindowContext window)
    {
        this.service = service;
        this.settings = settings;
        this.dialogs = dialogs;
        this.toasts = toasts;
        this.navigator = navigator;
        this.window = window;
        Status = service.IsConfigured ? "可检查新版本" : "此构建尚未配置更新来源";
        settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool HasUpdate { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial int DownloadProgress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(UpdateActionLabel))]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; }

    public bool CanCheck => service.IsConfigured && !IsBusy && !IsReady;
    public bool CanInstall => HasUpdate && !IsBusy;
    public string UpdateActionLabel => IsReady ? "重启并更新" : "下载更新";
    public bool CanOpenReleases => service.ReleasesPage is not null;
    public bool AutomaticallyCheck => settings.Current.AutomaticallyCheckForUpdates;

    public async Task CheckOnStartupAsync()
    {
        if (!service.IsConfigured || !AutomaticallyCheck) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            if (!disposed && AutomaticallyCheck) await CheckAsync(automatic: true);
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckAsync(bool automatic = false)
    {
        if (!CanCheck || disposed) return;
        IsBusy = true;
        Status = "正在检查更新…";
        try
        {
            available = await service.CheckAsync(lifetime.Token);
            if (disposed) return;
            HasUpdate = available is not null;
            Status = available is null ? "当前已是最新版本" : $"发现新版本 v{available.Version}";
            if (automatic && available is not null)
                toasts.Show(ToastKind.Info, Status, "查看更新", () => navigator.Navigate(Route.Settings), TimeSpan.FromSeconds(8));
        }
        catch (AppUpdateException error) { if (!disposed) Status = error.Message; }
        catch (OperationCanceledException) { }
        finally { if (!disposed) IsBusy = false; }
    }

    public async Task SetAutomaticallyCheckAsync(bool enabled)
    {
        if (disposed || AutomaticallyCheck == enabled) return;
        try { await settings.UpdateAsync(value => value with { AutomaticallyCheckForUpdates = enabled }, lifetime.Token); }
        catch (AppException error) { if (!disposed) toasts.Show(ToastKind.Error, error.Error.Message); }
        catch (OperationCanceledException) { }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall || available is null || disposed) return;
        var update = available;
        IsBusy = true;
        try
        {
            if (prepared is not null)
            {
                if (!await dialogs.ConfirmAsync(new ConfirmRequest($"重启并更新到 v{prepared.Version}？",
                    "将结束播放并保存进度。更新完成后 Mambo 会自动重新打开，账号和设置会保留。", "重启并更新")) || disposed) return;
                Status = "正在准备重启…";
                await service.LaunchPreparedAsync(prepared, lifetime.Token);
                await window.CloseForUpdateAsync();
                return;
            }
            if (update.ComponentManifest is not null)
            {
                download = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                IsDownloading = true;
                DownloadProgress = 0;
                Status = "正在检查本地文件并下载更新…";
                var componentProgress = new Progress<int>(value => { if (!disposed) DownloadProgress = value; });
                prepared = await service.PrepareAsync(update, componentProgress, download.Token);
                if (disposed) return;
                IsReady = true;
                Status = $"更新已就绪（下载 {prepared.DownloadedBytes / 1048576.0:0.0} MB），重启即可使用新版";
                return;
            }
            if (!await dialogs.ConfirmAsync(new ConfirmRequest($"更新到 v{update.Version}？",
                "这个发行版本只提供完整安装包。下载后将打开安装向导，便携版会转为安装版。", "下载并安装")) || disposed) return;
            download = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            IsDownloading = true;
            DownloadProgress = 0;
            Status = "正在下载更新…";
            var progress = new Progress<int>(value => { if (!disposed) DownloadProgress = value; });
            var result = await service.DownloadAsync(update, progress, download.Token);
            download.Token.ThrowIfCancellationRequested();
            Status = "正在验证安装包…";
            await using (var file = File.OpenRead(result.InstallerPath))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, download.Token));
                if (!hash.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new AppUpdateException("安装包校验失败，请重新下载。");
            }
            if (disposed) return;
            Process.Start(new ProcessStartInfo(result.InstallerPath) { UseShellExecute = true });
            Status = "安装向导已打开，请按提示完成更新";
        }
        catch (AppUpdateException error)
        {
            if (!disposed) { prepared = null; IsReady = false; Status = error.Message; }
        }
        catch (OperationCanceledException) { if (!disposed) Status = "已取消下载，可稍后重试"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { if (!disposed) { prepared = null; IsReady = false; Status = "无法完成更新，请重试或从发行页面下载"; } }
        finally
        {
            download?.Dispose();
            download = null;
            if (!disposed) { IsDownloading = false; IsBusy = false; }
        }
    }

    public void CancelDownload() => download?.Cancel();

    public async Task OpenReleasesAsync()
    {
        if (service.ReleasesPage is { } uri) _ = await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void OnSettingsChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(AutomaticallyCheck));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        settings.Changed -= OnSettingsChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
