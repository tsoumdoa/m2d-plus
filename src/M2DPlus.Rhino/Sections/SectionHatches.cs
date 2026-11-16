using System.Drawing;
using M2DPlus.Core.Sections;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>Pattern indices are assigned only at an explicit receiving File3dm boundary.</summary>
public static class SectionHatches
{
    public static HatchPattern CreatePattern(PatternDefinition definition)
    {
        var pattern = new HatchPattern { Name = definition.Name,
            FillType = definition.Solid ? HatchPatternFillType.Solid : HatchPatternFillType.Lines };
        foreach (var item in definition.Lines)
        {
            using var line = new HatchLine { Angle = item.Angle, BasePoint = new Point2d(item.Base.X, item.Base.Y),
                Offset = new Vector2d(item.Offset.X, item.Offset.Y) };
            line.SetDashes(item.Dashes);
            pattern.AddHatchLine(line);
        }
        return pattern;
    }

    internal static Hatch? Build(Brep region, SectionStyleSnapshot style, Plane plane, Plane? patternPlane = null)
    {
        if (style.Pattern is null && style.Background == BackgroundMode.None) return null;
        var curves = region.Loops.OrderBy(l => l.LoopType == BrepLoopType.Outer ? 0 : 1).Select(l => l.To3dCurve()).ToArray();
        Hatch? hatch;
        try
        {
            foreach (var curve in curves) curve.Transform(Transform.PlaneToPlane(plane, Plane.WorldXY));
            hatch = Hatch.Create(plane, curves[0], curves.Skip(1), 0, style.Rotation, style.Scale);
            if (hatch is not null) hatch.BasePoint = (patternPlane ?? plane).PointAt(style.PatternOrigin.X, style.PatternOrigin.Y);
        }
        finally { foreach (var curve in curves) curve.Dispose(); }
        if (hatch is null || !hatch.IsValid) { hatch?.Dispose(); throw new SectionFailure("Hatch construction failed."); }
        // A compute hatch has a placeholder index. Definition-by-value travels in SectionRegion.Style.
        return hatch;
    }

    /// <summary>Detached export for bake/save-reopen tests. Caller owns file and returned geometry copies.</summary>
    public static Guid AddToFile(File3dm file, SectionRegion region)
    {
        if (region.Hatch is null) return Guid.Empty;
        var definition = region.Style.Pattern ?? new PatternDefinition("Solid", true, Array.Empty<PatternLine>());
        using var pattern = CreatePattern(definition);
        pattern.Name = $"M2D-{Guid.NewGuid():N}"; // No name-only deduplication against unrelated document definitions.
        file.AllHatchPatterns.Add(pattern);
        var inserted = file.AllHatchPatterns.FindName(pattern.Name) ?? throw new InvalidOperationException("Pattern insertion failed.");
        using var hatch = (Hatch)region.Hatch.Duplicate();
        hatch.PatternIndex = inserted.Index;
        using var attributes = new ObjectAttributes { ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = Color.FromArgb(unchecked((int)(region.Style.Pattern is null ? region.Style.BackgroundColour : region.Style.PatternColour))),
            PlotColorSource = ObjectPlotColorSource.PlotColorFromObject,
            PlotColor = Color.FromArgb(unchecked((int)(region.Style.Pattern is null ? region.Style.BackgroundPrintColour : region.Style.PatternPrintColour))) };
        if (region.Style.Background != BackgroundMode.None)
            attributes.HatchBackgroundFillColor = Color.FromArgb(unchecked((int)region.Style.BackgroundColour));
        attributes.SetUserString("M2D.Region", region.Id);
        return file.Objects.AddHatch(hatch, attributes);
    }
}
