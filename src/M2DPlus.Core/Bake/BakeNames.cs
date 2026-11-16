using System.Collections.Immutable;
using System.Globalization;
using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Bake;

public sealed record DrawingNameRequest(ViewSourceKind Source, Plane3 Plane, bool IsCut,
    string? SourceName = null, string? OverrideName = null, int SectionNumber = 1);

/// <summary>The bake layer of one piece (UX §8.1): its full path from the <c>M2D+</c> root, and the first level that takes the
/// line type's styling (print width, Hidden linetype, <c>m2d.weights</c>). Levels above it are plain grouping layers.</summary>
public sealed record BakeLayerPath(ImmutableArray<string> Segments, int StyledFrom)
{
    public string FullPath => string.Join("::", Segments);
    public bool IsStyled(int level) => level >= StyledFrom;
    public override string ToString() => FullPath;
}

public static class BakeNames
{
    /// <summary>The root of every bake layer. Anything under it is never scene geometry (UX §3.1).</summary>
    public const string Root = "M2D+";
    /// <summary>The source-layer group for Grasshopper geometry, which has no Rhino layer (every layout but the classic one).</summary>
    public const string Grasshopper = "Grasshopper (no layer)";

    public static ImmutableArray<BakeLayout> Layouts { get; } = [BakeLayout.ViewLayer, BakeLayout.LayerView, BakeLayout.Layer, BakeLayout.ViewType];

    /// <summary>Settings text of a layout: <c>view-layer</c> · <c>layer-view</c> · <c>layer</c> · <c>view-type</c>.</summary>
    public static string LayoutText(BakeLayout layout) => layout switch
    {
        BakeLayout.ViewLayer => "view-layer", BakeLayout.LayerView => "layer-view", BakeLayout.Layer => "layer", BakeLayout.ViewType => "view-type",
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    public static string Category(LineClass lineClass) => lineClass switch
    {
        LineClass.Visible or LineClass.Profile => "Visible", LineClass.Hidden => "Hidden", LineClass.Beyond => "Beyond",
        LineClass.FarClip => "Far clip", LineClass.Cut => "Cut", LineClass.Hatch => "Hatch", LineClass.Unresolved => "Unresolved",
        LineClass.Ignored => "Ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(lineClass))
    };

    /// <summary>The bake layer of a piece (owner decision 2026-11-16, UX §8.1). The line type is the leaf:
    /// <list type="bullet">
    /// <item><c>view-layer</c> (default): <c>M2D+ › drawing › source layer path… › type</c></item>
    /// <item><c>layer-view</c>: <c>M2D+ › source layer path… › drawing › type</c></item>
    /// <item><c>layer</c>: <c>M2D+ › source layer path… › type</c>; every drawing shares these layers</item>
    /// <item><c>view-type</c> (classic, beta.3 and earlier): <c>M2D+ › drawing › type › source layer path…</c></item>
    /// </list>
    /// Source layer paths are mirrored as nested sublayers. Grasshopper geometry has no source layer: it takes the
    /// <see cref="Grasshopper"/> group at the source-layer level, except in the classic layout, where it goes straight
    /// onto the type layer as before. Classic styles the type layer and its source sublayers; the others style the leaf.</summary>
    public static BakeLayerPath Layer(BakeLayout layout, string drawing, LineClass lineClass, string? sourceLayerPath = null)
    {
        ValidateSegment(drawing);
        var source = string.IsNullOrEmpty(sourceLayerPath) ? [] : sourceLayerPath.Split("::", StringSplitOptions.None);
        foreach (var segment in source) ValidateSegment(segment);
        if (source.Length == 0 && layout != BakeLayout.ViewType) source = [Grasshopper];
        var type = Category(lineClass);
        ImmutableArray<string> segments = layout switch
        {
            BakeLayout.ViewLayer => [Root, drawing, .. source, type],
            BakeLayout.LayerView => [Root, .. source, drawing, type],
            BakeLayout.Layer => [Root, .. source, type],
            BakeLayout.ViewType => [Root, drawing, type, .. source],
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
        return new(segments, layout == BakeLayout.ViewType ? 2 : segments.Length - 1);
    }

    /// <summary>The classic (<c>view-type</c>) path; kept for callers and files from beta.3 and earlier.</summary>
    public static string LayerPath(string drawing, LineClass lineClass, string? sourceLayerPath = null) =>
        Layer(BakeLayout.ViewType, drawing, lineClass, sourceLayerPath).FullPath;

    /// <summary>The line type a classic (<c>view-type</c>) layer path is styled as, or null for the root and drawing levels
    /// or a path not under <see cref="Root"/>. Used to recognise unchanged layers M2D+ created before style signatures.</summary>
    public static LineClass? ClassicCategory(string fullPath)
    {
        var parts = fullPath.Split("::", StringSplitOptions.None);
        if (parts.Length < 3 || parts[0] != Root) return null;
        foreach (var lineClass in (LineClass[])[LineClass.Visible, LineClass.Hidden, LineClass.Beyond, LineClass.FarClip, LineClass.Cut,
            LineClass.Hatch, LineClass.Unresolved, LineClass.Ignored])
            if (Category(lineClass) == parts[2]) return lineClass;
        return null;
    }

    public static string Infer(DrawingNameRequest request, DocumentUnits units)
    {
        _ = Settings.MetresPerUnit(units);
        if (request.OverrideName is not null) { ValidateSegment(request.OverrideName); return request.OverrideName; }
        if (request.Source is ViewSourceKind.NamedView or ViewSourceKind.ClippingPlane && !string.IsNullOrWhiteSpace(request.SourceName))
        { ValidateSegment(request.SourceName); return request.SourceName; }
        if (request.SectionNumber < 1) throw new ArgumentOutOfRangeException(nameof(request));
        var normal = request.Plane.Z;
        if (Math.Abs(normal.Z) > 1 - 1e-8) return $"Plan {FormatElevation(request.Plane.Origin.Z, units)}";
        if (request.IsCut) return $"Section {request.SectionNumber}";
        if (Math.Abs(normal.Z) < 1e-8)
        {
            var facing = Math.Abs(normal.X) > Math.Abs(normal.Y) ? normal.X > 0 ? "East" : "West" : normal.Y > 0 ? "North" : "South";
            return $"Elevation {facing}";
        }
        return $"Axo {(normal.Y >= 0 ? "N" : "S")}{(normal.X >= 0 ? "E" : "W")}";
    }

    /// <summary>Metres use signed two decimals (Plan +1.20); other units include an explicit suffix.</summary>
    public static string FormatElevation(double value, DocumentUnits units)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var decimals = units == DocumentUnits.Millimetres ? 0 : 2;
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        var suffix = units switch { DocumentUnits.Metres => "", DocumentUnits.Millimetres => " mm", DocumentUnits.Centimetres => " cm",
            DocumentUnits.Kilometres => " km", DocumentUnits.Inches => " in", DocumentUnits.Feet => " ft", _ => throw new ArgumentOutOfRangeException(nameof(units)) };
        return (rounded < 0 ? "-" : "+") + Math.Abs(rounded).ToString($"F{decimals}", CultureInfo.InvariantCulture) + suffix;
    }

    public static ImmutableArray<string> Unique(IEnumerable<string> names, IEnumerable<string>? reserved = null)
    {
        var used = new HashSet<string>(reserved ?? [], StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var name in names)
        {
            ValidateSegment(name);
            var candidate = name; var suffix = 2;
            while (!used.Add(candidate)) candidate = $"{name} ({suffix++})";
            result.Add(candidate);
        }
        return result.ToImmutable();
    }

    internal static void ValidateSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("::", StringComparison.Ordinal) || name.Any(char.IsControl))
            throw new ArgumentException("Layer and drawing names must be nonempty and contain no :: or control characters.", nameof(name));
    }
}
