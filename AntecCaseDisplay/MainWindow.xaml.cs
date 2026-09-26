using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AntecCaseDisplay.Services;
using Microsoft.Win32;

namespace AntecCaseDisplay;

public partial class MainWindow : Window
{
    private Config _editing;
    private IReadOnlyList<HwInfoReader.Reading> _lastReadings = Array.Empty<HwInfoReader.Reading>();
    private bool _suppressUiEvents = true;

    private static readonly int[] DashHistoryOptions = { 30, 60, 120, 300, 600 };
    private const int DashMaxColumns = 8;
    private readonly ObservableCollection<DashboardItem> _dashItems = new();
    private string[] _dashDevices = Array.Empty<string>();

    public MainWindow()
    {
        InitializeComponent();
        _editing = App.Current.Config.Clone();

        PopulateStaticCombos();
        DashItemsGrid.ItemsSource = _dashItems;
        LoadFromConfig();
        _dashItems.CollectionChanged += (_, _) => PreviewDashboard();

        App.Current.Monitor.StatusChanged += OnMonitorStatus;
        App.Current.DashboardSettingsChanged += OnDashboardSettingsChanged;
        Closed += (_, _) =>
        {
            App.Current.Monitor.StatusChanged -= OnMonitorStatus;
            App.Current.DashboardSettingsChanged -= OnDashboardSettingsChanged;
            // Saved or not, the dashboard goes back to what's on disk.
            App.Current.PreviewDashboard(null);
        };

        _suppressUiEvents = false;
    }

    private void PopulateStaticCombos()
    {
        foreach (var t in Enum.GetValues<HwInfoReader.SensorType>())
        {
            CpuTypeCombo.Items.Add(t);
            GpuTypeCombo.Items.Add(t);
        }
        foreach (var a in Enum.GetValues<SensorAggregation>())
        {
            CpuAggCombo.Items.Add(a);
            GpuAggCombo.Items.Add(a);
        }
        foreach (var t in Enum.GetValues<AppTheme>())
        {
            ThemeCombo.Items.Add(t);
        }

        DashColumnsCombo.Items.Add("Auto (fit to window)");
        for (int i = 1; i <= DashMaxColumns; i++) DashColumnsCombo.Items.Add(i.ToString(CultureInfo.CurrentCulture));
        foreach (var sec in DashHistoryOptions)
        {
            DashHistoryCombo.Items.Add(sec < 60 ? $"{sec} seconds" : sec == 60 ? "1 minute" : $"{sec / 60} minutes");
        }
    }

    private void LoadFromConfig()
    {
        _suppressUiEvents = true;
        try
        {
            CpuTypeCombo.SelectedItem  = _editing.Cpu.SensorType;
            CpuPatternBox.Text         = _editing.Cpu.NamePattern;
            CpuAggCombo.SelectedItem   = _editing.Cpu.Aggregation;
            CpuScaleBox.Text           = _editing.Cpu.Scale.ToString(CultureInfo.InvariantCulture);
            CpuAlertBox.Text           = _editing.Cpu.AlertThreshold?.ToString(CultureInfo.InvariantCulture) ?? "";

            GpuTypeCombo.SelectedItem  = _editing.Gpu.SensorType;
            GpuPatternBox.Text         = _editing.Gpu.NamePattern;
            GpuAggCombo.SelectedItem   = _editing.Gpu.Aggregation;
            GpuScaleBox.Text           = _editing.Gpu.Scale.ToString(CultureInfo.InvariantCulture);
            GpuAlertBox.Text           = _editing.Gpu.AlertThreshold?.ToString(CultureInfo.InvariantCulture) ?? "";

            RefreshSlider.Value         = Math.Clamp(_editing.UpdateIntervalMs, (int)RefreshSlider.Minimum, (int)RefreshSlider.Maximum);
            UpdateRefreshLabel(RefreshSlider.Value);
            ReconnectBox.Text           = _editing.ReconnectIntervalMs.ToString(CultureInfo.InvariantCulture);
            IntegerCheck.IsChecked      = _editing.IntegerTemperatures;
            VerboseCheck.IsChecked      = _editing.Verbose;

            AlertsEnabledCheck.IsChecked = _editing.AlertsEnabled;
            AlertCooldownBox.Text        = _editing.AlertMinIntervalSeconds.ToString(CultureInfo.InvariantCulture);

            LoggingEnabledCheck.IsChecked = _editing.LoggingEnabled;
            LogPathBox.Text               = _editing.LogPath;

            ThemeCombo.SelectedItem        = _editing.Theme;
            AutoStartCheck.IsChecked       = AutoStartService.IsEnabled();
            StartMinimizedCheck.IsChecked  = _editing.StartMinimized;

            var dash = _editing.Dashboard;
            DashEnabledCheck.IsChecked     = dash.Enabled;
            DashTopmostCheck.IsChecked     = dash.AlwaysOnTop;
            DashBorderlessCheck.IsChecked  = dash.Borderless;
            DashColumnsCombo.SelectedIndex = Math.Clamp(dash.Columns, 0, DashMaxColumns);
            DashHistoryCombo.SelectedIndex = NearestHistoryIndex(dash.HistorySeconds);
            _dashItems.Clear();
            foreach (var item in dash.Items) _dashItems.Add(item);
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void OnMonitorStatus(MonitorService.Status s)
    {
        Dispatcher.BeginInvoke(() =>
        {
            HwInfoStatusText.Text   = s.HwInfoConnected  ? "connected"     : "not connected";
            DisplayStatusText.Text  = s.DisplayConnected ? "connected"     : "not connected";
            LiveCpuText.Text        = s.CpuValue is null ? "--"            : $"{s.CpuValue.Value:F1}°C";
            LiveGpuText.Text        = s.GpuValue is null ? "--"            : $"{s.GpuValue.Value:F1}°C";
            ErrorText.Text          = s.LastError ?? "";

            // Refresh the picker dropdowns whenever we get a fresh snapshot,
            // but don't clobber the user's current selection — and skip any
            // combo the user has open right now, otherwise the clear+refill
            // snaps its scroll position back to the selected item every tick.
            if (s.AllReadings.Count > 0)
            {
                _lastReadings = s.AllReadings;
                if (!CpuSensorCombo.IsDropDownOpen)
                    PopulateSensorCombo(CpuSensorCombo, (HwInfoReader.SensorType?)CpuTypeCombo.SelectedItem ?? HwInfoReader.SensorType.Temperature);
                if (!GpuSensorCombo.IsDropDownOpen)
                    PopulateSensorCombo(GpuSensorCombo, (HwInfoReader.SensorType?)GpuTypeCombo.SelectedItem ?? HwInfoReader.SensorType.Temperature);
                RefreshDashDevices();
            }
        });
    }

    private void PopulateSensorCombo(ComboBox combo, HwInfoReader.SensorType type)
    {
        // Repopulating the combo would otherwise fire SelectionChanged and
        // overwrite the user's pattern via the auto-fill handler.
        var wasSuppressed = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            var prevText = (combo.SelectedItem as HwInfoReader.Reading?)?.OriginalName;
            combo.Items.Clear();
            foreach (var r in _lastReadings)
            {
                if (r.Type != type) continue;
                combo.Items.Add(r);
            }
            combo.DisplayMemberPath = nameof(HwInfoReader.Reading.OriginalName);

            if (prevText is not null)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (((HwInfoReader.Reading)combo.Items[i]!).OriginalName == prevText)
                    {
                        combo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        finally
        {
            _suppressUiEvents = wasSuppressed;
        }
    }

    private void OnDashboardSettingsChanged()
    {
        // Shown/hidden or toggled from the tray or the dashboard's own menu;
        // reflect that so Save doesn't undo it (the checkboxes then re-preview).
        var live = App.Current.Config.Dashboard;
        DashEnabledCheck.IsChecked     = live.Enabled;
        DashTopmostCheck.IsChecked     = live.AlwaysOnTop;
        DashBorderlessCheck.IsChecked  = live.Borderless;
    }

    /// <summary>Dashboard settings as currently shown in this window.</summary>
    private DashboardConfig ReadDashboardFromUi()
    {
        var dash = new DashboardConfig
        {
            Enabled        = DashEnabledCheck.IsChecked == true,
            AlwaysOnTop    = DashTopmostCheck.IsChecked == true,
            Borderless     = DashBorderlessCheck.IsChecked == true,
            Columns        = Math.Max(0, DashColumnsCombo.SelectedIndex),
            HistorySeconds = DashHistoryOptions[Math.Max(0, DashHistoryCombo.SelectedIndex)],
            Items          = _dashItems.ToList(),
        };
        App.Current.CaptureDashboardPlacement(dash);
        return dash;
    }

    /// <summary>Shows unsaved dashboard edits on the dashboard right away.</summary>
    private void PreviewDashboard()
    {
        if (_suppressUiEvents) return;
        App.Current.PreviewDashboard(ReadDashboardFromUi());
    }

    private void OnDashSettingChanged(object sender, RoutedEventArgs e) => PreviewDashboard();

    private void OnDashCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // The edited value reaches the item only after this event; preview once it has.
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(new Action(PreviewDashboard), DispatcherPriority.Background);
    }

    private static int NearestHistoryIndex(int seconds)
    {
        int best = 0;
        for (int i = 1; i < DashHistoryOptions.Length; i++)
        {
            if (Math.Abs(DashHistoryOptions[i] - seconds) < Math.Abs(DashHistoryOptions[best] - seconds)) best = i;
        }
        return best;
    }

    /// <summary>Refills the device picker only when HWiNFO's device list
    /// actually changes (e.g. HWiNFO restarted), not on every tick.</summary>
    private void RefreshDashDevices()
    {
        if (DashDeviceCombo.IsDropDownOpen) return;

        var devices = _lastReadings.Select(r => r.SensorName).Where(n => n.Length > 0).Distinct().ToArray();
        if (devices.SequenceEqual(_dashDevices)) return;
        _dashDevices = devices;

        var previous = DashDeviceCombo.SelectedItem as string;
        DashDeviceCombo.ItemsSource = devices;
        DashDeviceCombo.SelectedItem = previous is not null && devices.Contains(previous) ? previous : devices.FirstOrDefault();
        PopulateDashReadings();
    }

    private void PopulateDashReadings()
    {
        var device = DashDeviceCombo.SelectedItem as string;
        var previous = DashReadingCombo.SelectedItem as HwInfoReader.Reading?;
        var readings = _lastReadings.Where(r => r.SensorName == device).ToList();
        DashReadingCombo.ItemsSource = readings;

        var keep = previous is { } p ? readings.FindIndex(r => r.Type == p.Type && r.OriginalName == p.OriginalName) : -1;
        DashReadingCombo.SelectedIndex = keep >= 0 ? keep : (readings.Count > 0 ? 0 : -1);
    }

    // ---- event handlers ----

    private void OnDashDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateDashReadings();
    }

    private void OnDashAddClicked(object sender, RoutedEventArgs e)
    {
        if (DashReadingCombo.SelectedItem is not HwInfoReader.Reading r)
        {
            ErrorText.Text = "Pick a reading to add first (HWiNFO must be running to list them).";
            return;
        }
        if (_dashItems.Any(i => i.Matches(r)))
        {
            ErrorText.Text = $"\"{r.OriginalName}\" is already on the dashboard.";
            return;
        }
        if (!EndDashGridEdit()) return;

        var item = DashboardItem.FromReading(r);
        _dashItems.Add(item);
        DashItemsGrid.SelectedItem = item;
        DashItemsGrid.ScrollIntoView(item);
        ErrorText.Text = "Added — Apply or Save to keep it.";
    }

    private void OnDashMoveUpClicked(object sender, RoutedEventArgs e) => MoveDashItem(-1);

    private void OnDashMoveDownClicked(object sender, RoutedEventArgs e) => MoveDashItem(+1);

    private void MoveDashItem(int delta)
    {
        int i = DashItemsGrid.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _dashItems.Count || !EndDashGridEdit()) return;
        _dashItems.Move(i, j);
        DashItemsGrid.SelectedIndex = j;
    }

    private void OnDashRemoveClicked(object sender, RoutedEventArgs e)
    {
        int i = DashItemsGrid.SelectedIndex;
        if (i < 0 || !EndDashGridEdit()) return;
        _dashItems.RemoveAt(i);
        DashItemsGrid.SelectedIndex = Math.Min(i, _dashItems.Count - 1);
    }

    /// <summary>Commits an in-progress cell edit; the collection can't be
    /// reordered mid-edit and Save should pick up what's typed.</summary>
    private bool EndDashGridEdit()
    {
        if (DashItemsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true)) return true;
        ErrorText.Text = "Fix the highlighted dashboard cell first (numbers only; leave blank to turn a threshold off).";
        return false;
    }

    private void OnCpuTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        PopulateSensorCombo(CpuSensorCombo, (HwInfoReader.SensorType)(CpuTypeCombo.SelectedItem ?? HwInfoReader.SensorType.Temperature));
    }

    private void OnGpuTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        PopulateSensorCombo(GpuSensorCombo, (HwInfoReader.SensorType)(GpuTypeCombo.SelectedItem ?? HwInfoReader.SensorType.Temperature));
    }

    private void OnCpuSensorPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (CpuSensorCombo.SelectedItem is HwInfoReader.Reading r)
        {
            CpuPatternBox.Text = "^" + System.Text.RegularExpressions.Regex.Escape(r.OriginalName) + "$";
        }
    }

    private void OnGpuSensorPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (GpuSensorCombo.SelectedItem is HwInfoReader.Reading r)
        {
            GpuPatternBox.Text = "^" + System.Text.RegularExpressions.Regex.Escape(r.OriginalName) + "$";
        }
    }

    private void OnRefreshSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRefreshLabel(e.NewValue);
    }

    private void UpdateRefreshLabel(double ms)
    {
        // Slider.ValueChanged fires during XAML parsing (when Maximum is set,
        // the value gets coerced against the new range) — at that point named
        // controls below the slider in the markup haven't been created yet.
        if (RefreshSliderLabel is null) return;

        var rounded = (int)Math.Round(ms / 100.0) * 100;
        RefreshSliderLabel.Text = rounded < 1000
            ? $"{rounded} ms"
            : $"{rounded / 1000.0:0.0} s";
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (ThemeCombo.SelectedItem is AppTheme t) ThemeManager.Apply(t);
    }

    private void OnRefreshSensorsClicked(object sender, RoutedEventArgs e)
    {
        // Sensor list updates automatically on the next StatusChanged tick.
        // This button just gives users a way to feel like they're forcing it.
        ErrorText.Text = "Refreshing… (next tick will repopulate the sensor lists)";
    }

    private void OnBrowseLogClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(LogPathBox.Text) ? "antec-display.log" : LogPathBox.Text,
            OverwritePrompt = false,
        };
        if (dlg.ShowDialog(this) == true) LogPathBox.Text = dlg.FileName;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        // Discard changes — re-apply the persisted theme so the live preview
        // doesn't leave the wrong theme up.
        ThemeManager.Apply(App.Current.Config.Theme);
        Close();
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (TryCommit(out var msg))
        {
            ErrorText.Text = "Saved.";
        }
        else
        {
            ErrorText.Text = msg;
        }
    }

    private void OnSaveCloseClicked(object sender, RoutedEventArgs e)
    {
        if (TryCommit(out var msg)) Close();
        else ErrorText.Text = msg;
    }

    private bool TryCommit(out string error)
    {
        error = "";
        if (!EndDashGridEdit())
        {
            error = ErrorText.Text;
            return false;
        }
        try
        {
            _editing.Cpu.SensorType   = (HwInfoReader.SensorType)CpuTypeCombo.SelectedItem!;
            _editing.Cpu.NamePattern  = CpuPatternBox.Text.Trim();
            _editing.Cpu.Aggregation  = (SensorAggregation)CpuAggCombo.SelectedItem!;
            _editing.Cpu.Scale        = ParseDouble(CpuScaleBox.Text, 1.0);
            _editing.Cpu.AlertThreshold = ParseNullableDouble(CpuAlertBox.Text);

            _editing.Gpu.SensorType   = (HwInfoReader.SensorType)GpuTypeCombo.SelectedItem!;
            _editing.Gpu.NamePattern  = GpuPatternBox.Text.Trim();
            _editing.Gpu.Aggregation  = (SensorAggregation)GpuAggCombo.SelectedItem!;
            _editing.Gpu.Scale        = ParseDouble(GpuScaleBox.Text, 1.0);
            _editing.Gpu.AlertThreshold = ParseNullableDouble(GpuAlertBox.Text);

            _editing.UpdateIntervalMs       = (int)Math.Round(RefreshSlider.Value);
            _editing.ReconnectIntervalMs    = (int)ParseDouble(ReconnectBox.Text, 5000);
            _editing.IntegerTemperatures    = IntegerCheck.IsChecked == true;
            _editing.Verbose                = VerboseCheck.IsChecked == true;

            _editing.AlertsEnabled          = AlertsEnabledCheck.IsChecked == true;
            _editing.AlertMinIntervalSeconds= (int)ParseDouble(AlertCooldownBox.Text, 60);

            _editing.LoggingEnabled         = LoggingEnabledCheck.IsChecked == true;
            _editing.LogPath                = LogPathBox.Text.Trim();

            _editing.Theme                  = (AppTheme)ThemeCombo.SelectedItem!;
            _editing.StartMinimized         = StartMinimizedCheck.IsChecked == true;

            _editing.Dashboard              = ReadDashboardFromUi();

            // Persist + apply
            _editing.Save(Config.DefaultPath);
            App.Current.Config = _editing.Clone();

            // Auto-start lives outside the JSON file (registry).
            try
            {
                AutoStartService.SetEnabled(AutoStartCheck.IsChecked == true);
            }
            catch (Exception ex)
            {
                error = $"Saved settings, but auto-start change failed: {ex.Message}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not save: {ex.Message}";
            return false;
        }
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double? ParseNullableDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
}
