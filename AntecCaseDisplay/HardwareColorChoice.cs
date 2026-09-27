using System.Windows.Media;

namespace AntecCaseDisplay;

/// <summary>An entry in the dashboard table's Hardware drop-down.</summary>
public sealed record HardwareChoice(string Name, HardwareCategory Value);

/// <summary>A colour offered in the settings' hardware colour pickers.</summary>
public sealed class ColorSwatch
{
    public static readonly IReadOnlyList<ColorSwatch> Palette =
        HardwareCategories.Palette.Select(p => new ColorSwatch(p.Name, p.Hex)).ToArray();

    public ColorSwatch(string name, string hex)
    {
        Name = name;
        Hex = hex;
        Brush = HardwareCategories.CreateBrush(hex) ?? Brushes.Transparent;
    }

    public string Name { get; }
    public string Hex { get; }
    public Brush Brush { get; }
}

/// <summary>One row of the settings' hardware colour pickers.</summary>
public sealed class HardwareColorChoice
{
    public HardwareColorChoice(HardwareCategory category, string hex)
    {
        Category = category;
        Name = HardwareCategories.DisplayName(category);

        var match = ColorSwatch.Palette.FirstOrDefault(s => SameColor(s.Hex, hex));
        if (match is null)
        {
            // Hand-edited colour that isn't in the palette: keep it selectable.
            match = new ColorSwatch("Custom", hex);
            Swatches = ColorSwatch.Palette.Append(match).ToArray();
        }
        else
        {
            Swatches = ColorSwatch.Palette;
        }
        Selected = match;
    }

    public HardwareCategory Category { get; }
    public string Name { get; }
    public IReadOnlyList<ColorSwatch> Swatches { get; }
    public ColorSwatch Selected { get; set; }

    private static bool SameColor(string a, string b) =>
        HardwareCategories.TryParseColor(a, out var ca) &&
        HardwareCategories.TryParseColor(b, out var cb) &&
        ca == cb;
}
