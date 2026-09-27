using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntecCaseDisplay;

public enum SensorAggregation
{
    Average,
    Max,
    Min,
    First,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class SlotConfig
{
    /// <summary>HWiNFO sensor type to filter on (Temperature, Fan, Usage, Clock, ...).</summary>
    [JsonPropertyName("sensorType")]
    public HwInfoReader.SensorType SensorType { get; set; } = HwInfoReader.SensorType.Temperature;

    /// <summary>Case-insensitive regex matched against OriginalName / UserName. May match multiple sensors.</summary>
    [JsonPropertyName("namePattern")]
    public string NamePattern { get; set; } = "";

    /// <summary>How to combine multiple matched sensors into one number.</summary>
    [JsonPropertyName("aggregation")]
    public SensorAggregation Aggregation { get; set; } = SensorAggregation.Average;

    /// <summary>Multiplier applied to the final value (e.g. 0.01 to fit RPM into 0-99 range).</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; set; } = 1.0;

    /// <summary>Per-slot alert threshold; null disables.</summary>
    [JsonPropertyName("alertThreshold")]
    public double? AlertThreshold { get; set; }
}

/// <summary>One tile on the dashboard: a single HWiNFO reading.</summary>
public sealed class DashboardItem
{
    [JsonPropertyName("sensorType")]
    public HwInfoReader.SensorType SensorType { get; set; } = HwInfoReader.SensorType.Temperature;

    /// <summary>HWiNFO's original device name, e.g. "CPU [#0]: AMD Ryzen 7 7800X3D: Enhanced".</summary>
    [JsonPropertyName("sensor")]
    public string SensorName { get; set; } = "";

    /// <summary>HWiNFO's original reading label, e.g. "CPU (Tctl/Tdie)". Exact match.</summary>
    [JsonPropertyName("reading")]
    public string ReadingName { get; set; } = "";

    /// <summary>Caption shown on the tile.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("decimals")]
    public int Decimals { get; set; } = 0;

    /// <summary>Tile turns amber at or above this value; null disables.</summary>
    [JsonPropertyName("warnAt")]
    public double? WarnAt { get; set; }

    /// <summary>Tile turns red at or above this value; null disables.</summary>
    [JsonPropertyName("criticalAt")]
    public double? CriticalAt { get; set; }

    /// <summary>Full-scale value for the bar under the tile (bar runs from 0);
    /// null hides the bar.</summary>
    [JsonPropertyName("barMax")]
    public double? BarMax { get; set; }

    /// <summary>Hardware the tile is coloured as; Auto guesses from the
    /// device name.</summary>
    [JsonPropertyName("hardware")]
    public HardwareCategory Hardware { get; set; } = HardwareCategory.Auto;

    [JsonIgnore]
    public HardwareCategory ResolvedHardware =>
        Hardware == HardwareCategory.Auto ? HardwareCategories.Detect(SensorName)
        : Enum.IsDefined(Hardware) ? Hardware
        : HardwareCategory.Other; // out-of-range number in a hand-edited file

    /// <summary>Settings-table text, e.g. "GPU" or "GPU (auto)".</summary>
    [JsonIgnore]
    public string HardwareDisplay => Hardware == HardwareCategory.Auto
        ? $"{HardwareCategories.DisplayName(ResolvedHardware)} (auto)"
        : HardwareCategories.DisplayName(Hardware);

    [JsonIgnore]
    public string DisplaySource => $"{SensorName} › {ReadingName}";

    public bool Matches(in HwInfoReader.Reading r) =>
        r.Type == SensorType && r.OriginalName == ReadingName && r.SensorName == SensorName;

    /// <summary>Sensible per-type defaults for a freshly added reading.</summary>
    public static DashboardItem FromReading(in HwInfoReader.Reading r)
    {
        var item = new DashboardItem
        {
            SensorType = r.Type,
            SensorName = r.SensorName,
            ReadingName = r.OriginalName,
            Label = r.UserName.Length > 0 ? r.UserName : r.OriginalName,
            Decimals = r.Type switch
            {
                HwInfoReader.SensorType.Voltage => 3,
                HwInfoReader.SensorType.Current => 2,
                HwInfoReader.SensorType.Power => 1,
                _ => 0,
            },
        };
        switch (r.Type)
        {
            case HwInfoReader.SensorType.Temperature:
                item.WarnAt = 80;
                item.CriticalAt = 90;
                item.BarMax = 100;
                break;
            case HwInfoReader.SensorType.Usage:
                item.BarMax = 100;
                break;
        }
        return item;
    }
}

public sealed class DashboardConfig
{
    /// <summary>Show the dashboard window. Toggled from the tray too, so it
    /// reopens next launch if it was open when the app quit.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = false;

    /// <summary>Hide the Windows title bar; drag anywhere to move.</summary>
    [JsonPropertyName("borderless")]
    public bool Borderless { get; set; } = true;

    /// <summary>Tile columns; 0 picks a count from the window width.</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 0;

    /// <summary>How much history the sparkline on each tile covers.</summary>
    [JsonPropertyName("historySeconds")]
    public int HistorySeconds { get; set; } = 60;

    /// <summary>Colour tiles by the hardware they belong to (CPU, GPU, ...).</summary>
    [JsonPropertyName("hardwareColorMode")]
    public HardwareColorMode HardwareColorMode { get; set; } = HardwareColorMode.Full;

    /// <summary>"#RRGGBB" per hardware category; missing or unparseable
    /// entries fall back to the defaults.</summary>
    [JsonPropertyName("hardwareColors")]
    public Dictionary<HardwareCategory, string> HardwareColors { get; set; } = HardwareCategories.DefaultColors();

    public string ColorFor(HardwareCategory c) =>
        HardwareColors.TryGetValue(c, out var hex) && HardwareCategories.TryParseColor(hex, out _)
            ? hex
            : HardwareCategories.DefaultColor(c);

    // Window placement, remembered across runs so it comes back on the same monitor.
    [JsonPropertyName("left")]
    public double? Left { get; set; }

    [JsonPropertyName("top")]
    public double? Top { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; } = 820;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 480;

    [JsonPropertyName("maximized")]
    public bool Maximized { get; set; } = false;

    [JsonPropertyName("items")]
    public List<DashboardItem> Items { get; set; } = new();
}

public sealed class Config
{
    [JsonPropertyName("cpu")]
    public SlotConfig Cpu { get; set; } = new()
    {
        SensorType = HwInfoReader.SensorType.Temperature,
        NamePattern = @"^CPU \(Tctl/Tdie\)$|^CPU Package$|^Core \(Tctl/Tdie\)$|^CPU$",
        Aggregation = SensorAggregation.Average,
        AlertThreshold = 90.0,
    };

    [JsonPropertyName("gpu")]
    public SlotConfig Gpu { get; set; } = new()
    {
        SensorType = HwInfoReader.SensorType.Temperature,
        NamePattern = @"^GPU Temperature$|^GPU$|^GPU Core$|^GPU Hot Spot$",
        Aggregation = SensorAggregation.Average,
        AlertThreshold = 85.0,
    };

    [JsonPropertyName("updateIntervalMs")]
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>Run at above-normal priority (and opt out of Windows'
    /// efficiency mode) so the display and dashboard keep updating when a
    /// game or stress test saturates every core.</summary>
    [JsonPropertyName("highPriority")]
    public bool HighPriority { get; set; } = true;

    [JsonPropertyName("reconnectIntervalMs")]
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>Round to whole degrees (sends X.0). Off by default — the
    /// display's decimal point is always rendered, so the "tenths" digit
    /// might as well carry real information.</summary>
    [JsonPropertyName("integerTemperatures")]
    public bool IntegerTemperatures { get; set; } = false;

    [JsonPropertyName("alertsEnabled")]
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Don't fire the same alert more often than this.</summary>
    [JsonPropertyName("alertMinIntervalSeconds")]
    public int AlertMinIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("loggingEnabled")]
    public bool LoggingEnabled { get; set; } = false;

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = "antec-display.log";

    [JsonPropertyName("theme")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    [JsonPropertyName("autoStartWithWindows")]
    public bool AutoStartWithWindows { get; set; } = false;

    /// <summary>Start hidden in the tray instead of showing the settings window.</summary>
    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = true;

    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; } = false;

    [JsonPropertyName("dashboard")]
    public DashboardConfig Dashboard { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new Config();
            defaults.Save(path);
            return defaults;
        }

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<Config>(json, SerializerOptions)
                     ?? throw new InvalidDataException($"Failed to parse {path}");

        // Hand-edited files may null these out.
        loaded.Dashboard ??= new DashboardConfig();
        loaded.Dashboard.Items ??= new List<DashboardItem>();
        loaded.Dashboard.HardwareColors ??= HardwareCategories.DefaultColors();

        // Migrate the v1 flat schema (cpuSensorPattern / gpuSensorPattern) so
        // upgrading from the CLI build doesn't lose user-tuned regexes.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(loaded.Cpu.NamePattern) &&
                    root.TryGetProperty("cpuSensorPattern", out var cpuPat) &&
                    cpuPat.ValueKind == JsonValueKind.String)
                {
                    loaded.Cpu.NamePattern = cpuPat.GetString() ?? loaded.Cpu.NamePattern;
                }
                if (string.IsNullOrEmpty(loaded.Gpu.NamePattern) &&
                    root.TryGetProperty("gpuSensorPattern", out var gpuPat) &&
                    gpuPat.ValueKind == JsonValueKind.String)
                {
                    loaded.Gpu.NamePattern = gpuPat.GetString() ?? loaded.Gpu.NamePattern;
                }
            }
        }
        catch
        {
            // Migration is best-effort; defaults already cover most setups.
        }

        return loaded;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
    }

    public Config Clone()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        return JsonSerializer.Deserialize<Config>(json, SerializerOptions)!;
    }
}
