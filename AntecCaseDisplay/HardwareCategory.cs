using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AntecCaseDisplay;

/// <summary>Kind of hardware a dashboard reading belongs to, used to colour
/// its tile.</summary>
public enum HardwareCategory
{
    /// <summary>Work it out from HWiNFO's device name.</summary>
    Auto,
    Cpu,
    Gpu,
    Motherboard,
    Memory,
    Storage,
    Network,
    Other,
}

/// <summary>How strongly tiles are coloured by their hardware.</summary>
public enum HardwareColorMode
{
    /// <summary>Every tile uses the theme accent.</summary>
    Off,
    /// <summary>Coloured strip down the tile's left edge only.</summary>
    Strip,
    /// <summary>Strip, sparkline and bar all in the hardware colour.</summary>
    Full,
}

public static class HardwareCategories
{
    /// <summary>Every real category (no <see cref="HardwareCategory.Auto"/>),
    /// in the order the settings list them.</summary>
    public static readonly HardwareCategory[] All =
    {
        HardwareCategory.Cpu,
        HardwareCategory.Gpu,
        HardwareCategory.Motherboard,
        HardwareCategory.Memory,
        HardwareCategory.Storage,
        HardwareCategory.Network,
        HardwareCategory.Other,
    };

    public static string DisplayName(HardwareCategory c) => c switch
    {
        HardwareCategory.Auto        => "Auto",
        HardwareCategory.Cpu         => "CPU",
        HardwareCategory.Gpu         => "GPU",
        HardwareCategory.Motherboard => "Motherboard",
        HardwareCategory.Memory      => "Memory",
        HardwareCategory.Storage     => "Storage",
        HardwareCategory.Network     => "Network",
        _                            => "Other",
    };

    /// <summary>Default colours: mid tones that read on both the light and
    /// dark dashboard, steering clear of the amber / red used for alerts.</summary>
    public static string DefaultColor(HardwareCategory c) => c switch
    {
        HardwareCategory.Cpu         => "#3B82F6", // blue
        HardwareCategory.Gpu         => "#22C55E", // green
        HardwareCategory.Motherboard => "#8B5CF6", // violet
        HardwareCategory.Memory      => "#14B8A6", // teal
        HardwareCategory.Storage     => "#EC4899", // pink
        HardwareCategory.Network     => "#84CC16", // lime
        _                            => "#94A3B8", // slate
    };

    public static Dictionary<HardwareCategory, string> DefaultColors() =>
        All.ToDictionary(c => c, DefaultColor);

    /// <summary>Colours offered in the settings picker.</summary>
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Blue",    "#3B82F6"),
        ("Sky",     "#0EA5E9"),
        ("Cyan",    "#06B6D4"),
        ("Teal",    "#14B8A6"),
        ("Green",   "#22C55E"),
        ("Lime",    "#84CC16"),
        ("Yellow",  "#EAB308"),
        ("Orange",  "#F97316"),
        ("Red",     "#EF4444"),
        ("Pink",    "#EC4899"),
        ("Magenta", "#D946EF"),
        ("Violet",  "#8B5CF6"),
        ("Indigo",  "#6366F1"),
        ("Slate",   "#94A3B8"),
    };

    // HWiNFO device names look like "CPU [#0]: AMD Ryzen 7 7800X3D: Enhanced",
    // "GPU [#0]: NVIDIA GeForce RTX 4080", "S.M.A.R.T.: Samsung SSD 990 PRO",
    // "Drive: ...", "Network: Intel(R) Ethernet ...", "DDR5 DIMM [#0] ...",
    // "Memory Timings", "System: ASUS ...", or a board named after its
    // Super I/O chip, "ASUS ROG STRIX X670E-E (Nuvoton NCT6799D)".
    private static readonly (Regex Pattern, HardwareCategory Category)[] Rules =
    {
        (Rx(@"^GPU\b"), HardwareCategory.Gpu),
        (Rx(@"^CPU\b"), HardwareCategory.Cpu),
        (Rx(@"^(S\.M\.A\.R\.T\.|Drive\b)|\b(SSD|NVMe|HDD)\b"), HardwareCategory.Storage),
        (Rx(@"^Network\b"), HardwareCategory.Network),
        (Rx(@"\b(Memory|DIMM|DDR\d|SPD)\b"), HardwareCategory.Memory),
        (Rx(@"^System\b|\b(Nuvoton|NCT\d+\w*|ITE|IT\d{4}\w*|Fintek|F7\d{4}\w*|EC|Embedded Controller|ACPI|Chipset|Motherboard|Mainboard)\b"),
            HardwareCategory.Motherboard),
    };

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        var s = hex?.Trim().TrimStart('#');
        if (s is not { Length: 6 or 8 } ||
            !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }
        if (s.Length == 6) v |= 0xFF000000;
        color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Frozen brush for a colour string, or null if it doesn't parse.</summary>
    public static SolidColorBrush? CreateBrush(string? hex)
    {
        if (!TryParseColor(hex, out var color)) return null;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Not RegexOptions.Compiled: these only run when the tiles are rebuilt.
    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Best guess at the hardware behind an HWiNFO device name.</summary>
    public static HardwareCategory Detect(string? sensorName)
    {
        if (string.IsNullOrEmpty(sensorName)) return HardwareCategory.Other;
        foreach (var (pattern, category) in Rules)
        {
            if (pattern.IsMatch(sensorName)) return category;
        }
        return HardwareCategory.Other;
    }
}
