using Microsoft.UI.Xaml;

namespace Mambo.App.Shell;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>外观主题；默认跟随系统。持久化等待契约提供字段（R-016）。</summary>
public sealed class ThemeService
{
    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public ElementTheme ElementTheme => Mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public event EventHandler? Changed;

    public void Set(ThemeMode mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
