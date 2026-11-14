using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Styling;

namespace M2DPlus.Grasshopper.UI.Model;

public enum FieldKind { Distance, Choice, Toggle, Percent, Scale, Text }
public sealed record FieldOption(string Value, string Label);
public sealed record FieldSpec(string Key, string Label, string Section, FieldKind Kind, ImmutableArray<FieldOption> Options, string Tooltip);

/// <summary>Editor/menu vocabulary for the UX §6.3 keys: labels, choices, canonical text and display text.
/// Values are the Settings text syntax, so whatever the editor stores pastes into a Panel unchanged.</summary>
public static class SettingsCatalog
{
    public const string ViewRange = "View range", Lines = "Lines", Cuts = "Cuts", Placement = "Placement";
    private static FieldOption O(string value, string label) => new(value, label);
    private static readonly ImmutableArray<FieldOption> OnOff = [O("on", "On"), O("off", "Off")];

    public static ImmutableArray<FieldSpec> Fields { get; } =
    [
        new("depth", "Depth", ViewRange, FieldKind.Distance, [O("auto", "Auto")], "depth = auto | <distance>: the far plane, measured from the view plane. Auto: box height, clipping-plane depth, else the scene extent."),
        new("beyond", "Beyond", ViewRange, FieldKind.Distance, [O("auto", "Auto"), O("off", "Off"), O("1 m", "1 m"), O("3 m", "3 m"), O("10 m", "10 m")], "beyond = auto | off | <distance>: visible lines farther than this go to Beyond. Auto: 3 m in cut views, off otherwise."),
        new("farclip", "Far clip", ViewRange, FieldKind.Choice, [O("none", "No clip"), O("line", "Clip with line"), O("noline", "Clip without line")], "farclip = none | line | noline"),
        new("crop", "Crop", ViewRange, FieldKind.Choice, [O("auto", "Auto"), O("off", "Off")], "crop = auto | off: off turns off a named view's crop. Rectangles and boxes always crop."),
        new("fade", "Fade with distance", ViewRange, FieldKind.Toggle, OnOff, "fade = on | off (Revit's depth cueing)"),
        new("fade.start", "Start", ViewRange, FieldKind.Percent, [], "fade.start = 0–100 %: nearer lines keep full intensity."),
        new("fade.end", "End", ViewRange, FieldKind.Percent, [], "fade.end = 0–100 %: lines at or beyond this reach the limit."),
        new("fade.limit", "Limit", ViewRange, FieldKind.Percent, [], "fade.limit = 0–100 %: the minimum intensity."),
        new("hidden", "Hidden lines", Lines, FieldKind.Toggle, OnOff, "hidden = on | off"),
        new("tangents", "Tangent edges", Lines, FieldKind.Toggle, OnOff, "tangents = on | off"),
        new("seams", "Seams", Lines, FieldKind.Toggle, OnOff, "seams = on | off"),
        new("glass", "Glass", Lines, FieldKind.Choice, [O("opaque", "Opaque"), O("clear", "Clear")], "glass = opaque | clear"),
        new("weights", "Weights", Lines, FieldKind.Choice, [O("auto", "From scale"), O("layers", "From source layers")], "weights = auto | layers"),
        new("hatch", "Hatch", Cuts, FieldKind.Choice, [O("styles", "From section styles"), O("colour", "Object colour"), O("black", "Black"), O("none", "None")], "hatch = styles | colour | black | none. Styles: the Rhino 8 section style, else a solid fill in the object's colour."),
        new("cuts", "Cuts", Cuts, FieldKind.Choice, [O("separate", "Separate per object"), O("merge", "Merge touching (same style)")], "cuts = separate | merge"),
        new("scale", "Scale", Placement, FieldKind.Scale, [O("1:20", "1:20"), O("1:50", "1:50"), O("1:100", "1:100"), O("1:200", "1:200"), O("1:500", "1:500")], "scale = 1:n. Line weights only (Revit's View Scale): the drawing stays 1:1."),
        new("gap", "Gap", Placement, FieldKind.Distance, [O("auto", "Auto")], "gap = auto | <distance>: space between drawings laid out on one Target. Auto: 1/10 of the mean frame width."),
        new("name", "Name", Placement, FieldKind.Text, [], "name = <text>: the drawing's bake layer name. Empty: from the view."),
        new("tree", "Output tree", "Output", FieldKind.Choice, [O("branch", "Per source branch"), O("object", "Branch per object")], "tree = branch | object"),
    ];

    public static FieldSpec Spec(string key) => Fields.First(f => f.Key == key);

    /// <summary>Canonical text for comparing values: on/off for toggles, lower-case choices, 1:n scales.</summary>
    public static string Canonical(string key, string text)
    {
        var value = text.Trim();
        var lower = value.ToLowerInvariant();
        switch (Fields.FirstOrDefault(f => f.Key == key)?.Kind)
        {
            case FieldKind.Toggle:
                return lower is "on" or "true" or "yes" or "1" ? "on" : lower is "off" or "false" or "no" or "0" ? "off" : lower;
            case FieldKind.Choice: return lower;
            case FieldKind.Scale: return ScaleDenominator(value) is double n ? "1:" + Number(n) : lower;
            case FieldKind.Percent: return Percent(value) is double p ? Number(p) + " %" : lower;
            case FieldKind.Distance: return lower is "auto" or "off" ? lower : value;
            default: return key == "name" ? UnquoteName(value) : value;
        }
    }

    /// <summary>What a menu or table shows for a stored value (option label where there is one).</summary>
    public static string Display(string key, string text)
    {
        var canonical = Canonical(key, text);
        var spec = Fields.FirstOrDefault(f => f.Key == key);
        var option = spec?.Options.FirstOrDefault(o => Canonical(key, o.Value) == canonical);
        if (option is not null) return option.Label;
        return key == "name" && canonical == "auto" ? "Auto" : canonical;
    }

    public static bool IsOn(string key, string text) => Canonical(key, text) == "on";

    public static double? ScaleDenominator(string text)
    {
        var value = text.Trim();
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 2 || parts[0].Trim() != "1") return null;
            value = parts[1].Trim();
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n > 0 ? n : null;
    }

    public static double? Percent(string text)
    {
        var value = text.Trim();
        if (value.EndsWith('%')) value = value[..^1].TrimEnd();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 100 ? n : null;
    }

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string UnquoteName(string text)
    {
        if (!text.StartsWith('"')) return text;
        try { return JsonSerializer.Deserialize<string>(text) ?? text; } catch (JsonException) { return text; }
    }

    /// <summary>The preset strip under Scale (UX §6.1), from Core's LineWeightPreset. Two lines for narrow layouts.</summary>
    public static string WeightStrip(string scaleText, string weightsText, bool twoLines = false)
    {
        if (Canonical("weights", weightsText) == "layers") return "Line weights from source layers; scale has no effect.";
        if (ScaleDenominator(scaleText) is not double scale) return "Enter a scale such as 1:100.";
        var preset = new LineWeightPreset(scale);
        static string W(double mm) => mm.ToString("0.00", CultureInfo.InvariantCulture);
        var breakOrDot = twoLines ? "\n" : " · ";
        if (twoLines)
            return $"Line weights only ({preset.BandName} preset), mm:\n" +
                   $"Cut {W(preset[LineClass.Cut].PrintWidthMillimetres)} · Profile {W(preset[LineClass.Profile].PrintWidthMillimetres)} · Visible {W(preset[LineClass.Visible].PrintWidthMillimetres)}\n" +
                   $"Hidden {W(preset[LineClass.Hidden].PrintWidthMillimetres)} · Beyond {W(preset[LineClass.Beyond].PrintWidthMillimetres)}";
        return $"Cut {W(preset[LineClass.Cut].PrintWidthMillimetres)} · Profile {W(preset[LineClass.Profile].PrintWidthMillimetres)} · " +
               $"Visible {W(preset[LineClass.Visible].PrintWidthMillimetres)}{breakOrDot}Hidden {W(preset[LineClass.Hidden].PrintWidthMillimetres)} · " +
               $"Beyond {W(preset[LineClass.Beyond].PrintWidthMillimetres)} mm ({preset.BandName} preset)";
    }

    public static string SourceLabel(ViewSettingsContext view) => view.Source switch
    {
        ViewSourceKind.Rectangle => "Rectangle", ViewSourceKind.Plane => "Plane", ViewSourceKind.Box => "Box",
        ViewSourceKind.ClippingPlane => "Clipping plane", ViewSourceKind.NamedView => "Named view",
        ViewSourceKind.Vector => "Vector", _ => "Direction"
    } + (view.IsCut ? " · cuts" : "");
}
