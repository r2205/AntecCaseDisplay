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

    [JsonPropertyName("reconnectIntervalMs")]
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>Round to whole degrees (sends X.0). Most users prefer this.</summary>
    [JsonPropertyName("integerTemperatures")]
    public bool IntegerTemperatures { get; set; } = true;

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
