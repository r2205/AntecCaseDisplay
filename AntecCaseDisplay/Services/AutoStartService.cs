using System.Diagnostics;
using Microsoft.Win32;

namespace AntecCaseDisplay.Services;

/// <summary>
/// Per-user auto-start using HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Doesn't require admin and survives Windows updates better than Task Scheduler
/// for a simple user app.
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AntecCaseDisplay";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        if (key is null) return false;
        var existing = key.GetValue(ValueName) as string;
        if (string.IsNullOrEmpty(existing)) return false;

        // Only consider it "enabled" if it points to *this* exe; otherwise the
        // user has a stale entry from a different install location.
        var current = NormalizedExePath();
        return string.Equals(StripQuotes(existing), current, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Could not open HKCU Run key.");
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{NormalizedExePath()}\"", RegistryValueKind.String);
        }
        else
        {
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }

    private static string NormalizedExePath()
    {
        // Prefer the .exe path (Process.MainModule); fall back to the runtime's
        // exe location which is usually the same on net8.0-windows.
        var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
        return Path.GetFullPath(mainModule ?? Environment.ProcessPath ?? "");
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}
