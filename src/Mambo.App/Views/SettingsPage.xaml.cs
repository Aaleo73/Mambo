using Mambo.App.Shell;
using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.System;

namespace Mambo.App.Views;

public sealed partial class SettingsPage : UserControl, INavigablePage, IDisposable
{
    private const string NoticesFile = "THIRD_PARTY_NOTICES.md";
    private const string Licenses =
        "Mambo · GPL-3.0-or-later\nmpv / libmpv · GPL-2.0-or-later\nFFmpeg · GPL-3.0-or-later（构建配置）\n" +
        "Windows App SDK · Microsoft 软件许可条款\n.NET / CommunityToolkit.Mvvm · MIT\n" +
        "Serilog · Apache-2.0\n本应用使用 MiSans 字体 · MiSans 字体知识产权许可协议\n完整声明见发布目录 THIRD_PARTY_NOTICES.md 与 LICENSES。";
    private readonly WindowContext window;
    private readonly ToastService toasts;
    private readonly DialogService dialogs;

    public SettingsPage(SettingsViewModel viewModel, WindowContext window, ToastService toasts, DialogService dialogs, AppUpdateViewModel updates)
    {
        ViewModel = viewModel;
        Updates = updates;
        this.window = window;
        this.toasts = toasts;
        this.dialogs = dialogs;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public SettingsViewModel ViewModel { get; }
    public AppUpdateViewModel Updates { get; }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) => await Updates.CheckAsync();
    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e) => await Updates.InstallAsync();
    private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => Updates.CancelDownload();
    private async void OnReleasesClick(object sender, RoutedEventArgs e) => await Updates.OpenReleasesAsync();
    private async void OnAutomaticUpdateToggled(object sender, RoutedEventArgs e) =>
        await Updates.SetAutomaticallyCheckAsync(AutomaticUpdateSwitch.IsOn);

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created) ScrollState.Restore(Scroller, entry.VerticalOffset);
        _ = ViewModel.RefreshCacheSizeAsync();
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh()
    {
        _ = ViewModel.RefreshCacheSizeAsync();
    }

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // 断开按钮平时显示"已连接"；指针移上去、键盘聚焦或正在断开时显示实际动作。
    private bool disconnectHover;
    private void OnDisconnectPointerEntered(object sender, PointerRoutedEventArgs e) { disconnectHover = true; ApplyDisconnectLabel(); }
    private void OnDisconnectPointerExited(object sender, PointerRoutedEventArgs e) { disconnectHover = false; ApplyDisconnectLabel(); }
    private void OnDisconnectFocusChanged(object sender, RoutedEventArgs e) => ApplyDisconnectLabel();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.DisconnectText) or nameof(SettingsViewModel.CanDisconnect)) ApplyDisconnectLabel();
    }

    private void ApplyDisconnectLabel()
    {
        var acting = disconnectHover || DisconnectButton.FocusState == FocusState.Keyboard || !ViewModel.CanDisconnect;
        DisconnectLabel.Text = acting ? ViewModel.DisconnectText : "已连接";
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        await ViewModel.ConnectAsync(PasswordInput.Password);
        if (!ViewModel.HasLoginError) PasswordInput.Password = "";
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e) => await ViewModel.DisconnectAsync();
    private async void OnEmbeddedClick(object sender, RoutedEventArgs e) => await ViewModel.SetPlaybackModeAsync(PlaybackMode.Embedded);
    private async void OnExternalClick(object sender, RoutedEventArgs e) => await ViewModel.SetPlaybackModeAsync(PlaybackMode.External);
    private async void OnHdrAutoClick(object sender, RoutedEventArgs e) => await ViewModel.SetHdrAsync(HdrMode.Auto);
    private async void OnHdrAlwaysClick(object sender, RoutedEventArgs e) => await ViewModel.SetHdrAsync(HdrMode.Always);
    private async void OnHdrOffClick(object sender, RoutedEventArgs e) => await ViewModel.SetHdrAsync(HdrMode.Off);
    private async void OnHardwareToggled(object sender, RoutedEventArgs e) => await ViewModel.SetHardwareDecodingAsync(HardwareSwitch.IsOn);
    private async void OnAudioLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string code }) await ViewModel.SetAudioLanguageAsync(code);
    }
    private async void OnSubtitleLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string code }) await ViewModel.SetSubtitleLanguageAsync(code);
    }
    private async void OnThemeSystemClick(object sender, RoutedEventArgs e) => await ViewModel.SetThemeAsync(ThemeMode.System);
    private async void OnThemeLightClick(object sender, RoutedEventArgs e) => await ViewModel.SetThemeAsync(ThemeMode.Light);
    private async void OnThemeDarkClick(object sender, RoutedEventArgs e) => await ViewModel.SetThemeAsync(ThemeMode.Dark);
    private async void OnClearCacheClick(object sender, RoutedEventArgs e) => await ViewModel.ClearCacheAsync();
    private async void OnValidateMpvClick(object sender, RoutedEventArgs e) => await ViewModel.ValidateMpvAsync();

    private async void OnMpvKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.ValidateMpvAsync();
    }

    private async void OnPickMpvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(window.WindowId);
            picker.FileTypeFilter.Add(".exe");
            var result = await picker.PickSingleFileAsync();
            if (result is not null) await ViewModel.ChooseMpvAsync(result.Path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            toasts.Show(ToastKind.Error, "无法打开文件选择器");
        }
    }

    private async void OnLicensesClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, NoticesFile);
        if (File.Exists(path))
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (await Launcher.LaunchFileAsync(file)) return;
        }
        await dialogs.ConfirmAsync(new ConfirmRequest("第三方许可", Licenses, "关闭", cancelText: null));
    }

    private async void OnLogsClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpenLogs) return;
        try
        {
            if (Directory.Exists(ViewModel.LogDirectory) && await Launcher.LaunchFolderPathAsync(ViewModel.LogDirectory)) return;
            toasts.Show(ToastKind.Error, "无法打开日志目录");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            toasts.Show(ToastKind.Error, "无法打开日志目录");
        }
    }
}
