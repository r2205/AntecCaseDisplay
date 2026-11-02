using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Casember.Services;

/// <summary>
/// Per-user auto-start using HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Doesn't require admin and survives Windows updates better than Task Scheduler
/// for a simple user app.
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Casember";

    /// <summary>The value's name from when the app was called AntecCaseDisplay.</summary>
    private const string LegacyValueName = "AntecCaseDisplay";

    /// <summary>Moves a start-with-Windows entry left by AntecCaseDisplay over
    /// to this exe. Left alone, it would keep launching the old exe and the
    /// settings checkbox would show unticked.</summary>
    public static void MigrateLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null) return;
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        key.SetValue(ValueName, $"\"{NormalizedExePath()}\"", RegistryValueKind.String);
    }

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
