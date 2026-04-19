using System.Threading;
using System.Windows;
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
        }
    }

    public MonitorService Monitor => _monitor ?? throw new InvalidOperationException("Monitor not started.");
    public LogService Log => _log ?? throw new InvalidOperationException("Log not started.");

    public new static App Current => (App)Application.Current;

    private void OnStartup(object sender, StartupEventArgs e)
    {
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

        if (!_config.StartMinimized)
        {
            ShowSettingsWindow();
        }
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

    // -------- tray menu handlers (wired in App.xaml below via the tray's ContextMenu) --------

    // Tray menu actions are wired up via AppCommands (see App.xaml).
}
