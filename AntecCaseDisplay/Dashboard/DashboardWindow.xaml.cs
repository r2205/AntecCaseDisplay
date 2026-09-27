using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using AntecCaseDisplay.Services;

namespace AntecCaseDisplay.Dashboard;

public partial class DashboardWindow : Window
{
    private const int MaxHistorySamples = 600;

    private DashboardTile[] _tiles = Array.Empty<DashboardTile>();
    private HwInfoReader.Reading?[] _matched = Array.Empty<HwInfoReader.Reading?>();
    private Dictionary<(HwInfoReader.SensorType, string, string), int> _tileIndex = new();
    private int _configuredColumns;
    private bool? _borderless;

    public DashboardWindow(DashboardConfig settings, int updateIntervalMs)
    {
        InitializeComponent();
        RestorePlacement(settings);
        ApplyConfig(settings, updateIntervalMs);

        TilesHost.SizeChanged += (_, _) => UpdateLayoutMetrics();
        StateChanged += (_, _) => UpdateMaximizedMargin();

        App.Current.Monitor.StatusChanged += OnMonitorStatus;
        Closed += (_, _) => App.Current.Monitor.StatusChanged -= OnMonitorStatus;
    }

    /// <param name="updateIntervalMs">Monitor refresh interval, to size the
    /// sparkline history.</param>
    public void ApplyConfig(DashboardConfig d, int updateIntervalMs)
    {
        Topmost = d.AlwaysOnTop;
        TopmostMenuItem.IsChecked = d.AlwaysOnTop;
        BorderlessMenuItem.IsChecked = d.Borderless;
        ApplyChrome(d.Borderless);
        _configuredColumns = Math.Max(0, d.Columns);

        // Keep each tile's history when only its presentation changed.
        var capacity = Math.Clamp(
            (int)(d.HistorySeconds * 1000L / Math.Max(50, updateIntervalMs)), 10, MaxHistorySamples);
        var oldHistory = new Dictionary<(HwInfoReader.SensorType, string, string), SampleBuffer>();
        foreach (var t in _tiles)
        {
            if (t.History.Capacity == capacity) oldHistory.TryAdd(TileKey(t.Item), t.History);
        }

        // One shared frozen brush per hardware category.
        var hardwareBrushes = HardwareCategories.All.ToDictionary(
            c => c, c => (Brush)HardwareCategories.CreateBrush(d.ColorFor(c))!);

        var tiles = new List<DashboardTile>(d.Items.Count);
        var index = new Dictionary<(HwInfoReader.SensorType, string, string), int>();
        foreach (var item in d.Items)
        {
            var key = TileKey(item);
            if (!index.TryAdd(key, tiles.Count)) continue; // duplicate entry in the JSON
            tiles.Add(new DashboardTile(item,
                oldHistory.TryGetValue(key, out var h) ? h : new SampleBuffer(capacity),
                hardwareBrushes[item.ResolvedHardware], d.HardwareColorMode));
        }

        _tiles = tiles.ToArray();
        _matched = new HwInfoReader.Reading?[_tiles.Length];
        _tileIndex = index;
        TilesHost.ItemsSource = _tiles;
        EmptyHint.Visibility = _tiles.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutMetrics();
    }

    private static (HwInfoReader.SensorType, string, string) TileKey(DashboardItem i) =>
        (i.SensorType, i.SensorName, i.ReadingName);

    // ---- live data ----

    private void OnMonitorStatus(MonitorService.Status s)
    {
        Dispatcher.BeginInvoke(() => ApplyStatus(s));
    }

    private void ApplyStatus(MonitorService.Status s)
    {
        if (!s.HwInfoConnected)
        {
            StatusBannerText.Text = s.LastError ?? "Waiting for HWiNFO…";
            StatusBanner.Visibility = Visibility.Visible;
        }
        else
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }

        if (_tiles.Length == 0) return;

        // One pass over the snapshot; first match wins for duplicate names.
        Array.Clear(_matched);
        foreach (var r in s.AllReadings)
        {
            if (_tileIndex.TryGetValue((r.Type, r.SensorName, r.OriginalName), out var i) && _matched[i] is null)
            {
                _matched[i] = r;
            }
        }

        // Nothing is drawn while minimised, so just keep the charts' history going.
        var refreshText = WindowState != WindowState.Minimized;
        for (int i = 0; i < _tiles.Length; i++)
        {
            _tiles[i].Update(_matched[i], refreshText);
        }
    }

    // ---- layout ----

    private void UpdateLayoutMetrics()
    {
        int n = _tiles.Length;
        double w = TilesHost.ActualWidth > 0 ? TilesHost.ActualWidth : Width - 14;
        double h = TilesHost.ActualHeight > 0 ? TilesHost.ActualHeight : Height - 14;
        if (n == 0 || w <= 0 || h <= 0) return;

        int cols = _configuredColumns > 0 ? Math.Min(_configuredColumns, n) : BestColumnCount(n, w, h);
        int rows = (n + cols - 1) / cols;
        double tileW = w / cols - 10;  // tile Margin="5"
        double tileH = h / rows - 10;

        // Scale type with the tile so a full-screen dashboard on a second
        // monitor is readable from across the desk.
        double valueSize = Math.Clamp(Math.Min(tileH * 0.30, tileW * 0.20), 18, 160);
        double labelSize = Math.Clamp(valueSize * 0.30, 11, 32);

        SetResource("DashColumns", cols);
        SetResource("DashValueFontSize", Math.Round(valueSize));
        SetResource("DashLabelFontSize", Math.Round(labelSize));
    }

    private void SetResource(string key, object value)
    {
        if (!Equals(Resources[key], value)) Resources[key] = value;
    }

    /// <summary>Column count whose tiles come closest to a pleasant ~1.6:1 shape.</summary>
    private static int BestColumnCount(int n, double w, double h)
    {
        const double targetAspect = 1.6;
        int best = 1;
        double bestScore = double.MaxValue;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            double aspect = (w / cols) / (h / rows);
            // Penalise empty cells in the last row a little.
            double score = Math.Abs(Math.Log(aspect / targetAspect)) + 0.15 * (cols * rows - n);
            if (score < bestScore)
            {
                bestScore = score;
                best = cols;
            }
        }
        return best;
    }

    // ---- window chrome & placement ----

    private void ApplyChrome(bool borderless)
    {
        if (_borderless == borderless) return;
        _borderless = borderless;

        if (borderless)
        {
            WindowStyle = WindowStyle.None;
            // WindowChrome keeps a resize border without needing
            // AllowsTransparency (which would force CPU composition).
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }
        else
        {
            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.SingleBorderWindow;
        }
        UpdateMaximizedMargin();
    }

    private void UpdateMaximizedMargin()
    {
        // A maximised borderless window overhangs the monitor by the
        // (invisible) resize frame; pad it back in.
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = WindowState == WindowState.Maximized && _borderless == true
            ? new Thickness(7 + frame.Left, 7 + frame.Top, 7 + frame.Right, 7 + frame.Bottom)
            : new Thickness(7);
    }

    private void RestorePlacement(DashboardConfig d)
    {
        Width = Math.Max(MinWidth, d.Width);
        Height = Math.Max(MinHeight, d.Height);

        if (d.Left is { } left && d.Top is { } top && IsOnScreen(left, top, Width, Height))
        {
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (d.Maximized)
        {
            // Maximise once the HWND exists at the saved position, so it
            // maximises onto that monitor rather than the primary one.
            SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    public void SavePlacement(DashboardConfig d)
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            d.Left = Math.Round(bounds.Left);
            d.Top = Math.Round(bounds.Top);
            d.Width = Math.Round(bounds.Width);
            d.Height = Math.Round(bounds.Height);
        }
        d.Maximized = WindowState == WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        // Require a decent chunk to be visible in case a monitor was unplugged.
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screen, new Rect(left, top, width, height));
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 60;
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // ---- input ----

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks that bubble out of popups (context menu, tooltips).
        if (e.OriginalSource is Visual v && !IsAncestorOf(v)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
        }
        else if (WindowState == WindowState.Normal && e.ButtonState == MouseButtonState.Pressed)
        {
            // Drag from anywhere — there's no title bar in borderless mode.
            DragMove();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleMaximized();
            e.Handled = true;
        }
    }

    private void OnTopmostMenuClicked(object sender, RoutedEventArgs e)
    {
        var on = TopmostMenuItem.IsChecked;
        App.Current.UpdateDashboardSettings(d => d.AlwaysOnTop = on);
    }

    private void OnBorderlessMenuClicked(object sender, RoutedEventArgs e)
    {
        var on = BorderlessMenuItem.IsChecked;
        App.Current.UpdateDashboardSettings(d => d.Borderless = on);
    }

    private void OnToggleMaximizeMenuClicked(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnSettingsMenuClicked(object sender, RoutedEventArgs e) => App.Current.ShowSettingsWindow();

    private void OnCloseMenuClicked(object sender, RoutedEventArgs e) => Close();
}
