using M2DPlus.Core.Sections;
using Rhino;
using Rhino.DocObjects;

namespace M2DPlus.Rhino.Services;

/// <summary>Host-resolved values only. IDs include the root, each instance and definition leaf.</summary>
internal sealed record SourceSection(SectionStyleSnapshot Style, bool FromSectioner,
    Guid[] ObjectIds, Guid[] LayerIds, string Rule)
{
    internal static SourceSection Plain { get; } = new(SectionStyleSnapshot.Default(), false,
        Array.Empty<Guid>(), Array.Empty<Guid>(), "GH geometry: ink fallback");
    internal string Revision => System.Text.Json.JsonSerializer.Serialize(this);
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
            mode == ObjectSectionAttributesSource.FromParent ? $"{mode} -> {parent?.Rule ?? "layer fallback"}" : mode.ToString());
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
