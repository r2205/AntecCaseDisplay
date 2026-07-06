using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using AntecCaseDisplay.Services;
using Microsoft.Win32;

namespace AntecCaseDisplay;

public partial class MainWindow : Window
{
    private Config _editing;
    private IReadOnlyList<HwInfoReader.Reading> _lastReadings = Array.Empty<HwInfoReader.Reading>();
    private bool _suppressUiEvents = true;

    public MainWindow()
    {
        InitializeComponent();
        _editing = App.Current.Config.Clone();

        PopulateStaticCombos();
        LoadFromConfig();

        App.Current.Monitor.StatusChanged += OnMonitorStatus;
        Closed += (_, _) => App.Current.Monitor.StatusChanged -= OnMonitorStatus;

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
            MonitorErrorText.Text       = s.LastError ?? "";
            MonitorErrorText.Visibility = string.IsNullOrEmpty(s.LastError)
                ? Visibility.Collapsed : Visibility.Visible;

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

    // ---- event handlers ----

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
        ShowFeedback("Refreshing… (next tick will repopulate the sensor lists)", "MutedForegroundBrush");
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
            ShowFeedback("Saved.", "GoodBrush");
        }
        else
        {
            ShowFeedback(msg, "BadBrush");
        }
    }

    private void OnSaveCloseClicked(object sender, RoutedEventArgs e)
    {
        if (TryCommit(out var msg)) Close();
        else ShowFeedback(msg, "BadBrush");
    }

    private void ShowFeedback(string message, string brushKey)
    {
        ErrorText.Text = message;
        // Resource reference (not a direct brush) so it keeps tracking the theme.
        ErrorText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private bool TryCommit(out string error)
    {
        // Parse and validate everything up front — a typo must fail the save
        // loudly, not silently fall back to a default (the old behaviour could
        // e.g. disable an alert because of one mistyped character).
        var errors = new List<string>();

        var cpuPattern = ValidatePattern(CpuPatternBox.Text, "CPU pattern", errors);
        var cpuScale   = RequireNumber(CpuScaleBox.Text, "CPU scale", errors);
        var cpuAlert   = OptionalNumber(CpuAlertBox.Text, "CPU alert threshold", errors);

        var gpuPattern = ValidatePattern(GpuPatternBox.Text, "GPU pattern", errors);
        var gpuScale   = RequireNumber(GpuScaleBox.Text, "GPU scale", errors);
        var gpuAlert   = OptionalNumber(GpuAlertBox.Text, "GPU alert threshold", errors);

        var reconnectMs = RequireWholeNumber(ReconnectBox.Text, "Reconnect interval", 50, 3_600_000, errors);
        var cooldownS   = RequireWholeNumber(AlertCooldownBox.Text, "Min seconds between alerts", 1, 86_400, errors);

        if (errors.Count > 0)
        {
            error = "Not saved:\n" + string.Join("\n", errors);
            return false;
        }

        error = "";
        try
        {
            _editing.Cpu.SensorType   = (HwInfoReader.SensorType)CpuTypeCombo.SelectedItem!;
            _editing.Cpu.NamePattern  = cpuPattern;
            _editing.Cpu.Aggregation  = (SensorAggregation)CpuAggCombo.SelectedItem!;
            _editing.Cpu.Scale        = cpuScale;
            _editing.Cpu.AlertThreshold = cpuAlert;

            _editing.Gpu.SensorType   = (HwInfoReader.SensorType)GpuTypeCombo.SelectedItem!;
            _editing.Gpu.NamePattern  = gpuPattern;
            _editing.Gpu.Aggregation  = (SensorAggregation)GpuAggCombo.SelectedItem!;
            _editing.Gpu.Scale        = gpuScale;
            _editing.Gpu.AlertThreshold = gpuAlert;

            _editing.UpdateIntervalMs       = (int)Math.Round(RefreshSlider.Value);
            _editing.ReconnectIntervalMs    = reconnectMs;
            _editing.IntegerTemperatures    = IntegerCheck.IsChecked == true;
            _editing.Verbose                = VerboseCheck.IsChecked == true;

            _editing.AlertsEnabled          = AlertsEnabledCheck.IsChecked == true;
            _editing.AlertMinIntervalSeconds= cooldownS;

            _editing.LoggingEnabled         = LoggingEnabledCheck.IsChecked == true;
            _editing.LogPath                = LogPathBox.Text.Trim();

            _editing.Theme                  = (AppTheme)ThemeCombo.SelectedItem!;
            _editing.StartMinimized         = StartMinimizedCheck.IsChecked == true;

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

    // ---- commit-time validation ----

    private static double RequireNumber(string text, string field, List<string> errors)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            !double.IsNaN(v) && !double.IsInfinity(v))
        {
            return v;
        }
        errors.Add($"{field}: \"{text.Trim()}\" is not a number.");
        return 0;
    }

    private static double? OptionalNumber(string text, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            !double.IsNaN(v) && !double.IsInfinity(v))
        {
            return v;
        }
        errors.Add($"{field}: \"{text.Trim()}\" is not a number (leave blank to disable).");
        return null;
    }

    private static int RequireWholeNumber(string text, string field, int min, int max, List<string> errors)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
            v >= min && v <= max)
        {
            return v;
        }
        errors.Add($"{field}: must be a whole number between {min} and {max}.");
        return min;
    }

    private static string ValidatePattern(string text, string field, List<string> errors)
    {
        var pattern = text.Trim();
        if (pattern.Length == 0) return pattern; // empty leaves the slot showing dashes
        try
        {
            _ = new Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{field}: not a valid regex ({ex.Message}).");
        }
        return pattern;
    }
}
