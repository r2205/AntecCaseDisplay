using System.Windows;
using Microsoft.Win32;

namespace AntecCaseDisplay.Services;

public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri  = new("pack://application:,,,/Themes/Dark.xaml",  UriKind.Absolute);

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var effective = theme switch
        {
            AppTheme.Light => AppTheme.Light,
            AppTheme.Dark  => AppTheme.Dark,
            _              => DetectSystemTheme(),
        };

        var uri = effective == AppTheme.Dark ? DarkUri : LightUri;

        // Replace any previously applied theme dictionary.
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = app.Resources.MergedDictionaries[i].Source;
            if (src is not null && (src == LightUri || src == DarkUri))
            {
                app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { /* fall through */ }
        return AppTheme.Light;
    }
}
