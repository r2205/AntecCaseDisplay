using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("AntecCaseDisplay is already running. Look for the icon in your system tray.",
                    "AntecCaseDisplay", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _config = Config.Load(Config.DefaultPath, out var configWarning);
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

            if (configWarning is not null)
            {
                MessageBox.Show(configWarning, "AntecCaseDisplay settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            // A half-initialised app with OnExplicitShutdown would linger as an
            // invisible process holding the single-instance mutex, and every
            // relaunch would then claim it is "already running". Report and exit
            // instead of leaving a zombie only Task Manager can remove.
            ReportFatal("startup", ex);
            Shutdown();
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
