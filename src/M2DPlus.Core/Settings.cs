using System.Globalization;
using System.Text.RegularExpressions;

namespace M2DPlus.Core;

/// <summary>Settings text syntax and typed, host-independent resolution. The original syntax-only API is retained.</summary>
public static partial class Settings
{
    public sealed record Assignment(string View, string Key, string Value, int Line);

    /// <summary>Parses one GH branch (all text items joined); throws with a line number on malformed syntax.</summary>
    public static IReadOnlyList<Assignment> Parse(IEnumerable<string> items)
    {
        var result = new List<Assignment>();
        var view = "*";
        var lineNumber = 0;
        foreach (var raw in string.Join('\n', items).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            // [#2] is a view-index header, not a comment. Comments may follow a header.
            if (line.StartsWith('['))
            {
                var end = line.IndexOf(']');
                if (end <= 1)
                    throw new FormatException($"Line {lineNumber}: invalid view header.");
                var tail = line[(end + 1)..].Trim();
                if (tail.Length > 0 && !tail.StartsWith('#'))
                    throw new FormatException($"Line {lineNumber}: invalid view header.");
                view = line[1..end].Trim();
                if (view.Length == 0) throw new FormatException($"Line {lineNumber}: empty view header.");
                continue;
            }
            line = line.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
                throw new FormatException($"Line {lineNumber}: expected key = value.");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!KeyPattern().IsMatch(key) || value.Length == 0)
                throw new FormatException($"Line {lineNumber}: expected key = value.");
            result.Add(new(view, key.ToLowerInvariant(), value, lineNumber));
        }
        return result.AsReadOnly();
    }

    /// <summary>Converts a finite distance to document units. Bare numbers already use document units.
    /// The host supplies metres per document unit; Core never looks up a Rhino document.</summary>
    public static double ParseDistance(string text, double metresPerDocumentUnit)
    {
        if (!double.IsFinite(metresPerDocumentUnit) || metresPerDocumentUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(metresPerDocumentUnit));
        var match = DistancePattern().Match(text.Trim());
        if (!match.Success) throw new FormatException($"Invalid distance: {text}");
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException($"Invalid distance: {text}");
        var unit = match.Groups[2].Value.ToLowerInvariant();
        var metres = unit switch
        {
            "" => metresPerDocumentUnit,
            "mm" => 0.001, "cm" => 0.01, "m" => 1, "km" => 1000,
            "in" or "\"" => 0.0254, "ft" or "'" => 0.3048,
            _ => throw new FormatException($"Unknown distance unit: {unit}. {Suggest(unit, ["mm", "cm", "m", "km", "in", "ft"])}")
        };
        var distance = unit.Length == 0 ? number : number * (metres / metresPerDocumentUnit);
        if (!double.IsFinite(distance)) throw new FormatException($"Non-finite distance: {text}");
        return distance;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.]*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^([+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)\\s*([A-Za-z]+|['\"])?$")]
    private static partial Regex DistancePattern();
}
