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
        "mpv / libmpv · GPL-2.0-or-later\nFFmpeg · GPL-3.0-or-later\nWindows App SDK · MIT\n.NET · MIT\n" +
        "CommunityToolkit.Mvvm · MIT\nMicrosoft.Extensions.DependencyInjection · MIT\nSerilog · Apache-2.0\nMiSans · MiSans 字体许可协议";
    private readonly WindowContext window;
    private readonly ToastService toasts;
    private readonly DialogService dialogs;

    public SettingsPage(SettingsViewModel viewModel, WindowContext window, ToastService toasts, DialogService dialogs)
    {
        ViewModel = viewModel;
        this.window = window;
        this.toasts = toasts;
        this.dialogs = dialogs;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public void OnNavigatedTo(NavEntry entry, NavigationMode mode, bool created)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (created) ScrollState.Restore(Scroller, entry.VerticalOffset);
    }

    public void OnNavigatedFrom(NavEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.VerticalOffset = Scroller.VerticalOffset;
    }

    public void Refresh()
    {
    }

    public void Dispose() => ViewModel.Dispose();

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
    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(ThemeMode.System);
    private void OnThemeLightClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(ThemeMode.Light);
    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => ViewModel.SetTheme(ThemeMode.Dark);
    private async void OnClearCacheClick(object sender, RoutedEventArgs e) => await ViewModel.ClearCacheAsync();

    private async void OnPickMpvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(window.WindowId);
            picker.FileTypeFilter.Add(".exe");
            var result = await picker.PickSingleFileAsync();
            if (result is not null) ViewModel.MpvPath = result.Path;
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
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mambo", "logs");
        if (!Directory.Exists(path) || !await Launcher.LaunchFolderPathAsync(path))
            toasts.Show(ToastKind.Error, "无法打开日志目录");
    }
}
