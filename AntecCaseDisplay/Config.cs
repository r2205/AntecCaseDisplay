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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Loads settings from <paramref name="path"/>. Never throws: a missing file
    /// produces defaults (persisted best-effort), and an unreadable file is moved
    /// aside as "&lt;file&gt;.bad" and replaced with defaults. When something went
    /// wrong, <paramref name="warning"/> carries a user-facing description.
    /// </summary>
    public static Config Load(string path, out string? warning)
    {
        warning = null;

        if (!File.Exists(path))
        {
            var defaults = new Config();
            try
            {
                defaults.Save(path);
            }
            catch (Exception ex)
            {
                // Read-only install location and the like. Run on in-memory
                // defaults; the settings window reports the same problem if the
                // user tries to save.
                warning = $"Could not write default settings to {path} ({ex.Message}). " +
                          "Changes made in the settings window will not survive a restart.";
            }
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Config>(json, SerializerOptions)
                         ?? throw new InvalidDataException($"Failed to parse {path}");

            // Migrate the v1 flat schema (cpuSensorPattern / gpuSensorPattern) so
            // upgrading from the CLI build doesn't lose user-tuned regexes. A v1
            // file has no "cpu"/"gpu" objects, so the deserializer leaves those
            // properties at their (non-empty) defaults — the JSON itself is the
            // only way to tell "explicitly configured" apart from "defaulted".
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    MigrateV1Pattern(root, "cpu", "cpuSensorPattern", loaded.Cpu);
                    MigrateV1Pattern(root, "gpu", "gpuSensorPattern", loaded.Gpu);
                }
            }
            catch
            {
                // Migration is best-effort; defaults already cover most setups.
            }

            return loaded;
        }
        catch (Exception ex)
        {
            warning = QuarantineUnreadableFile(path, ex);
            return new Config();
        }
    }

    /// <summary>
    /// Applies a v1 flat sensor pattern (e.g. "cpuSensorPattern") to a v2 slot,
    /// unless the JSON explicitly configures the slot's own namePattern — an
    /// explicit v2 value always wins. Also selects First aggregation, because v1
    /// used "first match wins" rather than averaging.
    /// </summary>
    private static void MigrateV1Pattern(JsonElement root, string slotProperty, string v1Property, SlotConfig slot)
    {
        if (!root.TryGetProperty(v1Property, out var v1Pat) ||
            v1Pat.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(v1Pat.GetString()))
        {
            return; // nothing to migrate
        }

        if (root.TryGetProperty(slotProperty, out var slotObj) &&
            slotObj.ValueKind == JsonValueKind.Object &&
            slotObj.TryGetProperty("namePattern", out var v2Pat) &&
            v2Pat.ValueKind == JsonValueKind.String)
        {
            return; // slot pattern explicitly set in the v2 schema
        }

        slot.NamePattern = v1Pat.GetString()!;
        slot.Aggregation = SensorAggregation.First;
    }

    /// <summary>
    /// Moves a settings file that failed to load aside and writes fresh defaults
    /// in its place, so the next launch starts clean while the old file stays
    /// recoverable. Returns the warning to show the user.
    /// </summary>
    private static string QuarantineUnreadableFile(string path, Exception cause)
    {
        string disposition;
        try
        {
            var backup = path + ".bad";
            File.Move(path, backup, overwrite: true);
            disposition = $"The old file was kept as {Path.GetFileName(backup)}";
            try { new Config().Save(path); } catch { /* best effort */ }
        }
        catch
        {
            disposition = "The old file was left in place";
        }
        return $"Could not read settings file {Path.GetFileName(path)} ({cause.Message}). " +
               $"Defaults are in use. {disposition}.";
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);

        // Write-to-temp, flush to disk, then swap into place. A crash or power
        // cut mid-save can then never leave a half-written appsettings.json —
        // the real file is always either the old version or the new one.
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public Config Clone()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        return JsonSerializer.Deserialize<Config>(json, SerializerOptions)!;
    }
}
