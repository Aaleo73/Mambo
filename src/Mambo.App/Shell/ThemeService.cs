using Mambo.Core.Contracts;
using Microsoft.UI.Xaml;

namespace Mambo.App.Shell;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>从设置恢复外观，并同步原子设置更新。</summary>
public sealed class ThemeService : IDisposable
{
    private readonly ISettingsService settings;

    public ThemeService(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        ApplySettings();
        settings.Changed += OnSettingsChanged;
    }

    public ThemeMode Mode { get; private set; }

    public ElementTheme ElementTheme => Mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public event EventHandler? Changed;

    public void Dispose() => settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        var mode = settings.Current.ThemeMode switch
        {
            SettingsThemeMode.Light => ThemeMode.Light,
            SettingsThemeMode.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (mode == Mode) return;
        Mode = mode;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
