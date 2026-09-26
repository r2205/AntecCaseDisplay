using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AntecCaseDisplay.Dashboard;
using AntecCaseDisplay.Services;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace AntecCaseDisplay;

public partial class App : Application
{
    // Per-user (no "Global\" prefix) so we don't need admin to create it.
    private const string SingleInstanceMutexName = "AntecCaseDisplay.SingleInstance.A1F4D2";

    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private MonitorService? _monitor;
    private LogService? _log;
    private MainWindow? _settingsWindow;
    private DashboardWindow? _dashboard;
    private bool _exiting;
    private Config _config = new();

    public Config Config
    {
        get => _config;
        set
        {
            _config = value;
            _monitor?.UpdateConfig(value);
            _log?.Configure(value.LoggingEnabled, value.LogPath);
            ThemeManager.Apply(value.Theme);
            ApplyDashboard();
        }
    }

    public MonitorService Monitor => _monitor ?? throw new InvalidOperationException("Monitor not started.");
    public LogService Log => _log ?? throw new InvalidOperationException("Log not started.");

    public new static App Current => (App)Application.Current;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Catch and surface anything that would otherwise terminate the process
        // silently. Without this, a XAML / binding / event-handler exception in
        // the settings window kills the whole app with no message.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal("UI thread", args.Exception);
            args.Handled = true; // keep the tray running
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ReportFatal("background", args.ExceptionObject as Exception
                                     ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal("task", args.Exception);
            args.SetObserved();
        };

        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("AntecCaseDisplay is already running. Look for the icon in your system tray.",
                "AntecCaseDisplay", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _config = Config.Load(Config.DefaultPath);
        ThemeManager.Apply(_config.Theme);

        _log = new LogService();
        _log.Configure(_config.LoggingEnabled, _config.LogPath);

        _monitor = new MonitorService(_config);
        _monitor.Log += msg => _log?.Write(msg);
        _monitor.AlertFired += OnAlertFired;
        _monitor.Start();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ForceCreate();
        UpdateTrayTooltip("Starting...");

        _monitor.StatusChanged += s =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                var cpu = s.CpuValue is null ? "--" : ((int)Math.Round(s.CpuValue.Value)).ToString();
                var gpu = s.GpuValue is null ? "--" : ((int)Math.Round(s.GpuValue.Value)).ToString();
                UpdateTrayTooltip($"CPU: {cpu}°C   GPU: {gpu}°C{(s.LastError is null ? "" : $"\n{s.LastError}")}");
            });
        };

        ApplyDashboard();

        if (!_config.StartMinimized)
        {
            ShowSettingsWindow();
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows is closing us: the dashboard's Closed handler must not
        // record that the user turned it off.
        _exiting = true;
    }

    public void Quit()
    {
        _exiting = true;
        Shutdown();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _monitor?.Stop();
        _monitor?.Dispose();
        _trayIcon?.Dispose();
        // We never owned the mutex (initiallyOwned: false), just held it open
        // to keep the named handle alive — Dispose closes it.
        _singleInstanceMutex?.Dispose();
    }

    private void OnAlertFired(string slot, double value, double threshold)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _trayIcon?.ShowNotification(
                    title: "AntecCaseDisplay alert",
                    message: $"{slot} is {value:F0}°C (threshold {threshold:F0}°C)",
                    icon: NotificationIcon.Warning);
            }
            catch { /* notifications can fail in odd shells, no big deal */ }
        });
    }

    private void UpdateTrayTooltip(string text)
    {
        if (_trayIcon is not null) _trayIcon.ToolTipText = text;
    }

    public void ShowSettingsWindow()
    {
        // Defer to a background dispatcher tick so the tray context-menu popup
        // has fully closed before we try to construct/show a window. Showing
        // a window from the menu's command handler synchronously can race with
        // popup dismissal and produce silent crashes.
        Dispatcher.BeginInvoke(new Action(ShowSettingsWindowCore), DispatcherPriority.Background);
    }

    private void ShowSettingsWindowCore()
    {
        try
        {
            if (_settingsWindow is null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new MainWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
            _settingsWindow.Focus();
        }
        catch (Exception ex)
        {
            ReportFatal("settings window", ex);
        }
    }

    // ---- dashboard ----

    public bool IsDashboardOpen => _dashboard is not null;

    /// <summary>Raised when dashboard settings change outside the settings
    /// window, so an open settings window can keep its checkboxes in sync.</summary>
    public event Action? DashboardSettingsChanged;

    /// <summary>Opens or closes the dashboard and remembers the choice.</summary>
    public void SetDashboardVisible(bool visible)
    {
        // Deferred for the same tray-menu reason as ShowSettingsWindow.
        Dispatcher.BeginInvoke(new Action(() => UpdateDashboardSettings(d => d.Enabled = visible)),
            DispatcherPriority.Background);
    }

    /// <summary>Tweaks dashboard settings outside the settings window (tray
    /// menu, the dashboard's own context menu), persists and applies them.</summary>
    public void UpdateDashboardSettings(Action<DashboardConfig> change)
    {
        change(_config.Dashboard);
        _dashboard?.SavePlacement(_config.Dashboard);
        SaveConfigQuietly();
        ApplyDashboard();
        DashboardSettingsChanged?.Invoke();
    }

    /// <summary>Copies the live window position into a config about to be
    /// saved, so saving settings doesn't rewind a moved dashboard.</summary>
    public void CaptureDashboardPlacement(DashboardConfig target)
    {
        if (_dashboard is not null)
        {
            _dashboard.SavePlacement(target);
            return;
        }
        var live = _config.Dashboard;
        target.Left = live.Left;
        target.Top = live.Top;
        target.Width = live.Width;
        target.Height = live.Height;
        target.Maximized = live.Maximized;
    }

    private void ApplyDashboard()
    {
        try
        {
            if (!_config.Dashboard.Enabled)
            {
                _dashboard?.Close();
                return;
            }

            if (_dashboard is null)
            {
                _dashboard = new DashboardWindow(_config);
                _dashboard.Closing += OnDashboardClosing;
                _dashboard.Closed += OnDashboardClosed;
                _dashboard.Show();
            }
            else
            {
                _dashboard.ApplyConfig(_config);
            }
        }
        catch (Exception ex)
        {
            ReportFatal("dashboard window", ex);
        }
    }

    private void OnDashboardClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // RestoreBounds (needed when maximised) is gone once the window has closed.
        (sender as DashboardWindow)?.SavePlacement(_config.Dashboard);
    }

    private void OnDashboardClosed(object? sender, EventArgs e)
    {
        if (sender is not DashboardWindow window) return;
        window.Closing -= OnDashboardClosing;
        window.Closed -= OnDashboardClosed;
        if (ReferenceEquals(_dashboard, window)) _dashboard = null;

        // Closed by the user (window menu, Alt+F4, tray) rather than by
        // quitting: keep it closed next launch too.
        if (!_exiting) _config.Dashboard.Enabled = false;
        SaveConfigQuietly();
        DashboardSettingsChanged?.Invoke();
    }

    private void SaveConfigQuietly()
    {
        try
        {
            _config.Save(Config.DefaultPath);
        }
        catch (Exception ex)
        {
            _log?.Write($"Could not save settings: {ex.Message}");
        }
    }

    private static void ReportFatal(string source, Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "antec-display-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* if we can't even log, fall through */ }

        try
        {
            MessageBox.Show(
                $"AntecCaseDisplay hit an error in the {source}:{Environment.NewLine}{Environment.NewLine}" +
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Full details were written to antec-display-error.log next to the exe.",
                "AntecCaseDisplay error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* if even MessageBox fails, give up */ }
    }
}
