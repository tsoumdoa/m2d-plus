using M2DPlus.Core.Sections;
using Rhino;
using Rhino.DocObjects;

namespace M2DPlus.Rhino.Services;

/// <summary>Host-resolved values only. IDs include the root, each instance and definition leaf.</summary>
internal sealed record SourceSection(SectionStyleSnapshot Style, bool FromSectioner,
    Guid[] ObjectIds, Guid[] LayerIds, string Rule)
{
    public string LayerPath { get; init; } = "";
    public double LayerWidth { get; init; }
    public uint LayerColour { get; init; } = 0xff000000;
    public bool Transparent { get; init; }
    internal static SourceSection Plain { get; } = new(SectionStyleSnapshot.Default(), false,
        Array.Empty<Guid>(), Array.Empty<Guid>(), "GH geometry: ink fallback");
    /// <summary>Every field, for the scene identity. The style part is serialised once per distinct style value.</summary>
    internal string Revision => StyleRevision(Style) + "|" + System.Text.Json.JsonSerializer.Serialize(this with { Style = StyleKey });
    private static readonly SectionStyleSnapshot StyleKey = SectionStyleSnapshot.Default(0, 0);
    private static readonly Dictionary<SectionStyleSnapshot, string> StyleRevisions = new();
    private static string StyleRevision(SectionStyleSnapshot style)
    {
        lock (StyleRevisions)
        {
            if (StyleRevisions.TryGetValue(style, out var known)) return known;
            if (StyleRevisions.Count > 4096) StyleRevisions.Clear();
            return StyleRevisions[style] = System.Text.Json.JsonSerializer.Serialize(style);
        }
    }
}

/// <summary>
/// Per-capture memo of <see cref="SectionCapture.Capture"/>: a part shared by many block instances is resolved once per
/// (object, parent style) instead of once per instance (colour, print colour, section style, material and layer
/// reads). Only the instance path (object and layer ids) differs between the instances, and that is appended here.
/// A capture is coherent (the document can't change during it without cancelling it), so the memo lives only as long
/// as one capture.
/// </summary>
internal sealed class SectionMemo(RhinoDoc document)
{
    private readonly Dictionary<SourceSection, int> _shapeOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(uint Serial, int Parent), (SourceSection Shape, Guid Layer, int Id)> _shapes = new();
    public int Hits { get; private set; }

    internal SourceSection Capture(RhinoObject obj, SourceSection? parent)
    {
        var parentShape = parent is null ? 0 : _shapeOf.GetValueOrDefault(parent, -1);
        if (parentShape < 0) return SectionCapture.Capture(document, obj, parent);
        var key = (obj.RuntimeSerialNumber, parentShape);
        if (_shapes.TryGetValue(key, out var entry)) Hits++;
        else
        {
            var full = SectionCapture.Capture(document, obj, parent);
            entry = (full with { ObjectIds = [], LayerIds = [] }, full.LayerIds[^1], _shapes.Count + 1);
            _shapes[key] = entry;
        }
        var result = entry.Shape with
        {
            ObjectIds = (parent?.ObjectIds ?? []).Append(obj.Id).ToArray(),
            LayerIds = (parent?.LayerIds ?? []).Append(entry.Layer).ToArray(),
        };
        _shapeOf[result] = entry.Id;
        return result;
    }
}

internal static class SectionCapture
{
    internal static SourceSection Capture(RhinoDoc document, RhinoObject obj, SourceSection? parent)
    {
        var attributes = obj.Attributes;
        var layer = document.Layers[attributes.LayerIndex];
        var colour = attributes.ColorSource == ObjectColorSource.ColorFromParent && parent is not null
            ? parent.Style.ObjectColour : unchecked((uint)attributes.DrawColor(document).ToArgb());
        var print = attributes.PlotColorSource == ObjectPlotColorSource.PlotColorFromParent && parent is not null
            ? parent.Style.ObjectPrintColour : unchecked((uint)attributes.ComputedPlotColor(document).ToArgb());
        using var custom = attributes.GetCustomSectionStyle();
        using var layerStyle = layer.GetCustomSectionStyle();
        var mode = attributes.SectionAttributesSource;
        var style = mode == ObjectSectionAttributesSource.FromParent && parent is not null
            ? parent.Style with { ObjectColour = colour, ObjectPrintColour = print }
            : Snapshot(document, mode == ObjectSectionAttributesSource.FromObject ? custom : layerStyle, colour, print);
        return new(style, mode == ObjectSectionAttributesSource.FromSectioner ||
            mode == ObjectSectionAttributesSource.FromParent && parent?.FromSectioner == true,
            (parent?.ObjectIds ?? Array.Empty<Guid>()).Append(obj.Id).ToArray(),
            (parent?.LayerIds ?? Array.Empty<Guid>()).Append(layer.Id).ToArray(),
            mode == ObjectSectionAttributesSource.FromParent ? $"{mode} -> {parent?.Rule ?? "layer fallback"}" : mode.ToString())
        {
            LayerPath = layer.FullPath, LayerWidth = layer.PlotWeight, LayerColour = unchecked((uint)layer.Color.ToArgb()),
            Transparent = obj.GetMaterial(true)?.Transparency > 0 || parent?.Transparent == true ||
                layer.FullPath.Split("::").Any(s => s.Contains("glass", StringComparison.OrdinalIgnoreCase) || s.Contains("glazing", StringComparison.OrdinalIgnoreCase))
        };
    }

    internal static SectionStyleSnapshot Snapshot(RhinoDoc document, SectionStyle? style, uint colour, uint print)
    {
        if (style is null) return SectionStyleSnapshot.Default(colour, print);
        PatternDefinition? definition = null;
        if (style.HatchIndex >= 0)
        {
            var pattern = document.HatchPatterns[style.HatchIndex];
            if (pattern is null || pattern.IsDeleted) throw new InvalidOperationException("Assigned section hatch pattern is unavailable.");
            var lines = new List<PatternLine>();
            for (var i = 0; i < pattern.HatchLineCount; i++)
            {
                using var line = pattern.HatchLineAt(i);
                lines.Add(new(line.Angle, new(line.BasePoint.X, line.BasePoint.Y), new(line.Offset.X, line.Offset.Y),
                    Enumerable.Range(0, line.DashCount).Select(line.DashAt).ToArray()));
            }
            definition = new(pattern.Name, pattern.FillType == HatchPatternFillType.Solid, lines.ToArray());
        }
        using var boundary = style.GetBoundaryLinetype();
        var segments = new List<double>();
        if (boundary is not null)
            for (var i = 0; i < boundary.SegmentCount; i++)
            { boundary.GetSegment(i, out var length, out var solid); segments.Add(solid ? length : -length); }
        static uint Argb(System.Drawing.Color c) => unchecked((uint)c.ToArgb());
        return new(true, definition, style.HatchScale, style.HatchRotationRadians,
            Argb(style.HatchPatternColor), Argb(style.HatchPatternPrintColor),
            style.BackgroundFillMode switch { SectionBackgroundFillMode.SolidColor => BackgroundMode.SolidColour,
                SectionBackgroundFillMode.Viewport => BackgroundMode.Viewport, _ => BackgroundMode.None },
            Argb(style.BackgroundFillColor), Argb(style.BackgroundFillPrintColor),
            new(style.BoundaryVisible, style.BoundaryWidthScale, Argb(style.BoundaryColor), Argb(style.BoundaryPrintColor),
                boundary?.Name ?? "Continuous", segments.ToArray()), colour, print, default);
    }
}
