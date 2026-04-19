using System.Text.RegularExpressions;

namespace AntecCaseDisplay.Services;

/// <summary>
/// Resolves a SlotConfig against a snapshot of HWiNFO readings: filters by
/// type, matches the regex (anywhere in OriginalName or UserName), aggregates
/// the matched values, applies the scale.
/// </summary>
public static class SensorResolver
{
    public readonly record struct Resolved(double? Value, IReadOnlyList<string> MatchedNames);

    public static Resolved Resolve(SlotConfig slot, IReadOnlyList<HwInfoReader.Reading> readings)
    {
        if (string.IsNullOrWhiteSpace(slot.NamePattern))
        {
            return new Resolved(null, Array.Empty<string>());
        }

        Regex regex;
        try
        {
            regex = new Regex(slot.NamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // Bad regex from the user — surface as "no match" rather than crashing.
            return new Resolved(null, Array.Empty<string>());
        }

        var matches = new List<(double Value, string Name)>();
        foreach (var r in readings)
        {
            if (r.Type != slot.SensorType) continue;
            if (regex.IsMatch(r.OriginalName) || (r.UserName.Length > 0 && regex.IsMatch(r.UserName)))
            {
                if (!double.IsNaN(r.Value) && !double.IsInfinity(r.Value))
                {
                    matches.Add((r.Value, r.OriginalName));
                }
            }
        }

        if (matches.Count == 0)
        {
            return new Resolved(null, Array.Empty<string>());
        }

        double agg = slot.Aggregation switch
        {
            SensorAggregation.Average => matches.Average(m => m.Value),
            SensorAggregation.Max => matches.Max(m => m.Value),
            SensorAggregation.Min => matches.Min(m => m.Value),
            SensorAggregation.First => matches[0].Value,
            _ => matches.Average(m => m.Value),
        };

        return new Resolved(agg * slot.Scale, matches.Select(m => m.Name).ToList());
    }
}
