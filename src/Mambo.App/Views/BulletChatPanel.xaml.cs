using Mambo.App.ViewModels;
using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mambo.App.Views;

/// <summary>播放页的弹幕面板；作为按钮的弹出层内容，每次打开加载、收起卸载。</summary>
public sealed partial class BulletChatPanel : UserControl, IDisposable
{
    private bool disposed;

    public BulletChatPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public BulletChatPanelViewModel? ViewModel { get; private set; }
    /// <summary>滑块拖动中的即时预览。</summary>
    public event EventHandler<BulletChatSettings>? PreviewChanged;
    /// <summary>手动选定了剧集，宿主应收起面板。</summary>
    public event EventHandler? Completed;

    public void Initialize(IBulletChatService service, ISettingsService settings, Func<string?> suggestion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ViewModel is not null) throw new InvalidOperationException("弹幕面板已初始化。");
        ViewModel = new(service, settings, suggestion, DispatcherQueue);
        ViewModel.PreviewChanged += OnPreviewChanged;
        ViewModel.Completed += OnCompleted;
        Bindings.Update();
    }

    private void OnPreviewChanged(object? sender, BulletChatSettings preview) => PreviewChanged?.Invoke(this, preview);
    private void OnCompleted(object? sender, EventArgs args) => Completed?.Invoke(this, EventArgs.Empty);
    private void OnLoaded(object sender, RoutedEventArgs args) => ViewModel?.Refresh();
    private void OnUnloaded(object sender, RoutedEventArgs args) => ViewModel?.Close();
    private void OnRetryClick(object sender, RoutedEventArgs args) => ViewModel?.Retry();
    private void OnBackClick(object sender, RoutedEventArgs args) => ViewModel?.Back();

    private void OnSearchClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel is null) return;
        ViewModel.OpenSearch();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || ViewModel is null) return;
        args.Handled = true;
        _ = ViewModel.SearchAsync();
    }

    private void OnRowClick(object sender, ItemClickEventArgs args)
    {
        if (ViewModel is not null && args.ClickedItem is BulletChatRow row) _ = ViewModel.OpenAsync(row);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        RetryButton.Click -= OnRetryClick;
        SearchButton.Click -= OnSearchClick;
        BackButton.Click -= OnBackClick;
        SearchBox.KeyDown -= OnSearchKeyDown;
        ResultList.ItemClick -= OnRowClick;
        Bindings.StopTracking();
        if (ViewModel is { } model)
        {
            model.PreviewChanged -= OnPreviewChanged;
            model.Completed -= OnCompleted;
            model.Dispose();
        }
        PreviewChanged = null;
        Completed = null;
    }
}
