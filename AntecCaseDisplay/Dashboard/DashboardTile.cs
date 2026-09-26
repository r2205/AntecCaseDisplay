using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace AntecCaseDisplay.Dashboard;

public enum TileState
{
    /// <summary>Reading not currently reported by HWiNFO.</summary>
    Missing,
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// View model for one dashboard tile. Setters only raise PropertyChanged when
/// the displayed text actually changes, so a steady reading costs no layout.
/// </summary>
public sealed class DashboardTile : INotifyPropertyChanged
{
    private string _valueText = "--";
    private string _unit = "";
    private string _rangeText = "";
    private TileState _state = TileState.Missing;
    private double _barFraction;
    private int _historyVersion;

    /// <param name="history">Passed in so a tile rebuilt after a settings
    /// change (new caption, thresholds, ...) keeps its chart.</param>
    public DashboardTile(DashboardItem item, SampleBuffer history)
    {
        Item = item;
        History = history;
        Label = string.IsNullOrWhiteSpace(item.Label) ? item.ReadingName : item.Label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DashboardItem Item { get; }
    public string Label { get; }
    public SampleBuffer History { get; }
    public bool HasBar => Item.BarMax is > 0;

    /// <summary>Usage readings are percentages; pin the chart to 0-100 so a
    /// 3% blip doesn't fill the tile.</summary>
    public double ChartMin => Item.SensorType == HwInfoReader.SensorType.Usage ? 0 : double.NaN;
    public double ChartMax => Item.SensorType == HwInfoReader.SensorType.Usage ? 100 : double.NaN;

    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string Unit { get => _unit; private set => Set(ref _unit, value); }
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }
    public TileState State { get => _state; private set => Set(ref _state, value); }
    public double BarFraction { get => _barFraction; private set => Set(ref _barFraction, value); }
    public int HistoryVersion { get => _historyVersion; private set => Set(ref _historyVersion, value); }

    /// <summary>Records a sample. <paramref name="refreshText"/> false only
    /// appends to the history (used while the window is minimised).</summary>
    public void Update(HwInfoReader.Reading? reading, bool refreshText)
    {
        var value = reading is { } r && double.IsFinite(r.Value) ? r.Value : double.NaN;
        History.Add(value);
        if (!refreshText) return;

        HistoryVersion++;

        if (reading is not { } rd || double.IsNaN(value))
        {
            ValueText = "--";
            RangeText = "";
            BarFraction = 0;
            State = TileState.Missing;
            return;
        }

        ValueText = Format(value);
        Unit = rd.Unit;
        RangeText = double.IsFinite(rd.Min) && double.IsFinite(rd.Max)
            ? $"{Format(rd.Min)} – {Format(rd.Max)}"
            : "";
        BarFraction = Item.BarMax is { } max && max > 0 ? Math.Clamp(value / max, 0, 1) : 0;
        State = Item.CriticalAt is { } crit && value >= crit ? TileState.Critical
              : Item.WarnAt is { } warn && value >= warn ? TileState.Warning
              : TileState.Normal;
    }

    private string Format(double v) =>
        v.ToString("F" + Math.Clamp(Item.Decimals, 0, 4), CultureInfo.CurrentCulture);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
