using System.Windows;
using ModernWpf;
using MwpfThemeManager = ModernWpf.ThemeManager;

namespace AntecCaseDisplay.Services;

public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is null) return;

        // ModernWpf takes ApplicationTheme.Light / Dark, or null to follow
        // the Windows app-theme setting. ThemeDictionaries keyed "Light" and
        // "Dark" in App.xaml swap along with its built-in brushes.
        MwpfThemeManager.Current.ApplicationTheme = theme switch
        {
            AppTheme.Light  => ApplicationTheme.Light,
            AppTheme.Dark   => ApplicationTheme.Dark,
            _               => null,
        };
    }
}
