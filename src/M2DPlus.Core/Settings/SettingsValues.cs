using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using M2DPlus.Core.Sections;

namespace M2DPlus.Core;

public static partial class Settings
{
    public const bool DefaultHidden = true;
    public static bool IsLegacyKey(string key) => key is "engine" or "joints";
    public static IEnumerable<string> ActiveKeys => Keys.Where(k => !IsLegacyKey(k));
    public static ImmutableArray<string> Keys { get; } = ["depth", "farclip", "beyond", "fade", "fade.start", "fade.end",
        "fade.limit", "crop", "hidden", "tangents", "seams", "joints", "glass", "scale", "weights", "hatch", "cuts", "gap", "name", "tree", "tolerance", "min_visible", "engine", "cutonly", "bake", "visibility"];

    /// <summary>Keys read only when baking (UX §8.1). A change to them alone republishes the current drawing: no
    /// visibility solve and no presentation (<see cref="RunText"/>).</summary>
    public static ImmutableArray<string> BakeTimeKeys { get; } = ["bake"];

    /// <summary>True for a <c>key = value</c> line whose key is bake-time or ignored legacy (comments and headers are not).</summary>
    public static bool IsBakeTimeLine(string line)
    {
        var text = line; var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash];
        var equals = text.IndexOf('=');
        return equals > 0 && (BakeTimeKeys.Contains(NormalizeKey(text[..equals])) || IsLegacyKey(NormalizeKey(text[..equals])));
    }

    /// <summary>Settings text with bake-time and ignored legacy lines left out: what a drawing run depends on. Two runs whose run text,
    /// views, inputs and scene are equal draw the same thing, whatever their <c>bake</c> values.</summary>
    public static string RunText(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines.SelectMany(l => l.Replace("\r\n", "\n").Split('\n')).Where(l => !IsBakeTimeLine(l)));
        return RasterRunPolicy(text);
    }
    private static string RasterRunPolicy(string text)
    {
        // Each header starts a per-view row; only its last valid assignment applies.
        var raster = false; var effective = "hld";
        foreach (var line in text.Split('\n'))
        {
            var clean = line.Split('#')[0].Trim();
            if (clean.StartsWith('[')) { raster |= effective == "raster"; effective = "hld"; }
            var equals = clean.IndexOf('=');
            if (equals <= 0 || NormalizeKey(clean[..equals]) != "visibility") continue;
            var value = clean[(equals + 1)..].Trim().ToLowerInvariant();
            if (value is "hld" or "pipes" or "raster") effective = value;
        }
        return raster || effective == "raster" ? text + "\n# " + Experimental.RasterVisibility.RasterPolicy.Identity : text;
    }

    /// <summary>A stored row (menu or view object) without bake-time and ignored legacy keys, as canonical text for run keys.</summary>
    public static string RunText(SettingsRow row) => RasterRunPolicy(string.Join("\n", row.Values
        .Where(p => !BakeTimeKeys.Contains(NormalizeKey(p.Key)) && !IsLegacyKey(NormalizeKey(p.Key))).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));

    /// <summary>The keys that change what the visibility solve computes, and so key the drawing cache (UX §12.14).
    /// Hidden is keyed by <see cref="EffectiveSettings.VisibilityKey"/>.
    /// Min-visible and styling remain presentation over the cached solve.</summary>
    public static ImmutableArray<string> VisibilityKeys { get; } = ["depth", "farclip", "crop", "hidden", "tangents", "seams", "glass", "tolerance", "cutonly", "visibility"];

    /// <summary>Canonical key spelling: lower case, runs of spaces as one underscore, so <c>min visible</c>,
    /// <c>Min_Visible</c> and the user-text key <c>m2d.min_visible</c> all name <c>min_visible</c>.</summary>
    public static string NormalizeKey(string key)
    {
        var normalized = WhitespaceRun().Replace(key.Trim().ToLowerInvariant(), "_");
        return normalized == "cut_only" ? "cutonly" : normalized;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRun();

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
        if (IsLegacyKey(key) && value == "default") return key == "engine" ? EngineMode.Exact : true;
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
            case "min_visible":
                // Off and 0 both mean off (the default). Otherwise a 2D length, in the drawing at 1:1.
                if (value == "off") return 0d;
                if (value.Length > 0 && char.IsLetter(value[0])) throw new FormatException(Suggest(value, ["off"]));
                var minimum = ParseDistance(text, units);
                if (minimum < 0) throw new FormatException("Distance must be nonnegative.");
                return minimum;
            case "tolerance":
                if (value is "document" or "auto") return DistanceSetting.Auto;
                if (value.Length > 0 && char.IsLetter(value[0])) throw new FormatException(Suggest(value, ["document", "auto"]));
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
            case "fade": case "hidden": case "tangents": case "seams": case "joints": case "cutonly":
                return value switch { "on" or "true" or "yes" or "1" => true, "off" or "false" or "no" or "0" => false,
                    _ => throw new FormatException(Suggest(value, ["on", "off", "true", "false", "yes", "no", "1", "0"])) };
            case "farclip": return Choice(value, ("none", FarClipMode.None), ("line", FarClipMode.Line), ("noline", FarClipMode.NoLine));
            case "crop": return Choice(value, ("auto", CropMode.Auto), ("off", CropMode.Off));
            case "glass": return Choice(value, ("opaque", GlassMode.Opaque), ("clear", GlassMode.Clear));
            case "visibility": return Choice(value, ("hld", VisibilityMode.Hld), ("pipes", VisibilityMode.Pipes), ("raster", VisibilityMode.Raster));
            case "engine": return value == "fast" ? EngineMode.Native : Choice(value, ("exact", EngineMode.Exact), ("native", EngineMode.Native));
            case "bake": return Choice(value, Bake.BakeNames.Layouts.Select(l => (Bake.BakeNames.LayoutText(l), l)).ToArray());
            case "weights": return Choice(value, ("auto", WeightMode.Auto), ("layers", WeightMode.Layers));
            case "hatch": return Choice(value, ("styles", HatchMode.Styles), ("colour", HatchMode.Colour), ("black", HatchMode.Black), ("none", HatchMode.None));
            case "cuts": return Choice(value, ("separate", CutMode.Separate), ("merge", CutMode.Merge));
            // "branch" (the pre-2026-11-15 default, one branch per Geometry input branch) reads as auto, the new default.
            case "tree": return value == "branch" ? OutputTreeMode.Auto
                : Choice(value, ("auto", OutputTreeMode.Auto), ("layer", OutputTreeMode.Layer), ("object", OutputTreeMode.Object));
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
        DistanceSetting d => d.Mode switch { DistanceMode.Auto => key == "tolerance" ? "document" : "auto", DistanceMode.Off => "off", _ => Numeric(d.Value) },
        bool b => b ? "on" : "off",
        double n => key == "min_visible" ? n == 0 ? "off" : Numeric(n) : key == "scale" ? $"1:{Numeric(n)}" : key.StartsWith("fade.", StringComparison.Ordinal) ? $"{Numeric(n)} %" : Numeric(n),
        string s => JsonSerializer.Serialize(s),
        BakeLayout layout => Bake.BakeNames.LayoutText(layout),
        _ => value.ToString()!.ToLowerInvariant()
    };
    private static string Numeric(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static object DefaultValue(string key, ViewSettingsContext view) => key switch
    {
        "depth" or "beyond" or "gap" => DistanceSetting.Auto,
        "farclip" => FarClipMode.NoLine, "fade" or "tangents" or "seams" or "cutonly" => false, "hidden" => DefaultHidden,
        "fade.start" => 0d, "fade.end" => 100d, "fade.limit" => 30d, "crop" => CropMode.Auto,
        "glass" => GlassMode.Opaque, "scale" => 100d, "weights" => WeightMode.Auto, "hatch" => HatchMode.Styles,
        "cuts" => CutMode.Separate, "name" => "auto", "tree" => OutputTreeMode.Auto, "tolerance" => DistanceSetting.Auto,
        "visibility" => VisibilityMode.Hld, "min_visible" => 0d, "engine" => EngineMode.Exact, "joints" => true, "bake" => BakeLayout.ViewLayer,
        _ => throw new ArgumentException("Unknown key.", nameof(key))
    };
}
