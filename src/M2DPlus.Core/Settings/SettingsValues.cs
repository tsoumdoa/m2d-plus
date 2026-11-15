using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using M2DPlus.Core.Sections;

namespace M2DPlus.Core;

public static partial class Settings
{
    public static ImmutableArray<string> Keys { get; } = ["depth", "farclip", "beyond", "fade", "fade.start", "fade.end",
        "fade.limit", "crop", "hidden", "tangents", "seams", "glass", "scale", "weights", "hatch", "cuts", "gap", "name", "tree", "tolerance"];

    public static double MetresPerUnit(DocumentUnits units) => units switch
    {
        DocumentUnits.Millimetres => .001, DocumentUnits.Centimetres => .01, DocumentUnits.Metres => 1,
        DocumentUnits.Kilometres => 1000, DocumentUnits.Inches => .0254, DocumentUnits.Feet => .3048,
        _ => throw new ArgumentOutOfRangeException(nameof(units))
    };
    public static double ParseDistance(string text, DocumentUnits units) => ParseDistance(text, MetresPerUnit(units));

    private static object ParseValue(string key, string text, DocumentUnits units)
    {
        var value = text.Trim().ToLowerInvariant();
        switch (key)
        {
            case "depth": case "beyond": case "gap":
                if (value == "auto") return DistanceSetting.Auto;
                if (key == "beyond" && value == "off") return DistanceSetting.Off;
                if (value.Length > 0 && char.IsLetter(value[0]))
                    throw new FormatException(Suggest(value, key == "beyond" ? ["auto", "off"] : ["auto"]));
                var distance = ParseDistance(text, units);
                if (distance < 0) throw new FormatException("Distance must be nonnegative.");
                return DistanceSetting.At(distance);
            case "tolerance":
                var tolerance = ParseDistance(text, units);
                if (tolerance <= 0) throw new FormatException("Tolerance must be positive.");
                return tolerance;
            case "fade.start": case "fade.end": case "fade.limit":
                var percent = Number(value.EndsWith('%') ? value[..^1].TrimEnd() : value);
                if (percent < 0 || percent > 100) throw new FormatException("Percentage must be between 0 and 100.");
                return percent;
            case "scale":
                var scaleText = value;
                if (value.Contains(':'))
                {
                    var parts = value.Split(':');
                    if (parts.Length != 2 || parts[0].Trim() != "1") throw new FormatException("Use 1:n or n.");
                    scaleText = parts[1].Trim();
                }
                var scale = Number(scaleText);
                if (scale <= 0) throw new FormatException("Scale denominator must be positive.");
                return scale;
            case "fade": case "hidden": case "tangents": case "seams":
                return value switch { "on" or "true" or "yes" or "1" => true, "off" or "false" or "no" or "0" => false,
                    _ => throw new FormatException(Suggest(value, ["on", "off", "true", "false", "yes", "no", "1", "0"])) };
            case "farclip": return Choice(value, ("none", FarClipMode.None), ("line", FarClipMode.Line), ("noline", FarClipMode.NoLine));
            case "crop": return Choice(value, ("auto", CropMode.Auto), ("off", CropMode.Off));
            case "glass": return Choice(value, ("opaque", GlassMode.Opaque), ("clear", GlassMode.Clear));
            case "weights": return Choice(value, ("auto", WeightMode.Auto), ("layers", WeightMode.Layers));
            case "hatch": return Choice(value, ("styles", HatchMode.Styles), ("colour", HatchMode.Colour), ("black", HatchMode.Black), ("none", HatchMode.None));
            case "cuts": return Choice(value, ("separate", CutMode.Separate), ("merge", CutMode.Merge));
            case "tree": return Choice(value, ("branch", OutputTreeMode.Branch), ("object", OutputTreeMode.Object));
            case "name":
                var name = text.StartsWith('"') ? JsonSerializer.Deserialize<string>(text) ?? "" : text.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.Contains("::", StringComparison.Ordinal))
                    throw new FormatException("Name must be nonempty, on one line, and contain no layer separator (::).");
                return name;
            default: throw new FormatException($"Unknown setting '{key}'. {Suggest(key, Keys)}");
        }
    }

    private static T Choice<T>(string value, params (string Name, T Value)[] choices) =>
        choices.FirstOrDefault(c => c.Name == value) is var selected && selected.Name is not null ? selected.Value
        : throw new FormatException(Suggest(value, choices.Select(c => c.Name)));

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("Use a finite number with a decimal point.");

    internal static string Suggest(string value, IEnumerable<string> choices)
    {
        var closest = choices.OrderBy(c => EditDistance(value.ToLowerInvariant(), c.ToLowerInvariant())).ThenBy(c => c, StringComparer.Ordinal).FirstOrDefault();
        return closest is null ? "No views exist." : $"Did you mean '{closest}'?";
    }

    private static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return row[b.Length];
    }

    internal static string FormatEffective(string key, object? value) => value is null ? "off" : value switch
    {
        DistanceSetting d => d.Mode switch { DistanceMode.Auto => "auto", DistanceMode.Off => "off", _ => Numeric(d.Value) },
        bool b => b ? "on" : "off",
        double n => key == "scale" ? $"1:{Numeric(n)}" : key.StartsWith("fade.", StringComparison.Ordinal) ? $"{Numeric(n)} %" : Numeric(n),
        string s => JsonSerializer.Serialize(s),
        _ => value.ToString()!.ToLowerInvariant()
    };
    private static string Numeric(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static object DefaultValue(string key, ViewSettingsContext view) => key switch
    {
        "depth" or "beyond" or "gap" => DistanceSetting.Auto,
        "farclip" => FarClipMode.NoLine, "fade" or "tangents" or "seams" => false, "hidden" => true,
        "fade.start" => 0d, "fade.end" => 100d, "fade.limit" => 30d, "crop" => CropMode.Auto,
        "glass" => GlassMode.Opaque, "scale" => 100d, "weights" => WeightMode.Auto, "hatch" => HatchMode.Styles,
        "cuts" => CutMode.Separate, "name" => "auto", "tree" => OutputTreeMode.Branch, "tolerance" => view.DocumentTolerance,
        _ => throw new ArgumentException("Unknown key.", nameof(key))
    };
}
