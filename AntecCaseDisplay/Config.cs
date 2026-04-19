using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntecCaseDisplay;

public sealed class Config
{
    /// <summary>
    /// Regex matched (case-insensitively) against a HWiNFO temperature reading's
    /// OriginalName and UserName. First match wins.
    /// </summary>
    [JsonPropertyName("cpuSensorPattern")]
    public string CpuSensorPattern { get; set; } = @"^CPU \(Tctl/Tdie\)$|^CPU Package$|^Core \(Tctl/Tdie\)$|^CPU$";

    [JsonPropertyName("gpuSensorPattern")]
    public string GpuSensorPattern { get; set; } = @"^GPU Temperature$|^GPU$|^GPU Core$|^GPU Hot Spot$";

    [JsonPropertyName("updateIntervalMs")]
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>
    /// How long to wait before retrying when HWiNFO or the display cannot be
    /// opened (e.g. the user has not started HWiNFO yet).
    /// </summary>
    [JsonPropertyName("reconnectIntervalMs")]
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>
    /// When true, the program prints each sensor reading it sends. Useful for
    /// figuring out the right regex for your system.
    /// </summary>
    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// When true, prints the list of HWiNFO temperature sensors on startup so
    /// you can tune the patterns above.
    /// </summary>
    [JsonPropertyName("listSensorsOnStart")]
    public bool ListSensorsOnStart { get; set; } = false;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new Config();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, SerializerOptions));
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Config>(json, SerializerOptions)
               ?? throw new InvalidDataException($"Failed to parse {path}");
    }
}
