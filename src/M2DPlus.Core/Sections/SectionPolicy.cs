namespace M2DPlus.Core.Sections;

public enum CutMode { Separate, Merge }
public enum HatchMode { Styles, Colour, Black, None }
public enum BackgroundMode { None, SolidColour, Viewport }
public enum SectionStatus { Crossing, Behind, InFront, Contact, OutlineOnly, Uncertain, Unsupported }
[Flags]
public enum EdgeOrigin { None = 0, PhysicalCut = 1, Crop = 2, Original = 4, NearTrim = 8, FarTrim = 16 }

public readonly record struct Point2(double X, double Y);
public sealed record PatternLine(double Angle, Point2 Base, Point2 Offset, IReadOnlyList<double> Dashes);
public sealed record PatternDefinition(string Name, bool Solid, IReadOnlyList<PatternLine> Lines);
public sealed record BoundaryDefinition(bool Visible, double WidthScale, uint Colour, uint PrintColour,
    string LinetypeName, IReadOnlyList<double> LinetypeSegments);

/// <summary>Resolved host values; no indices, live objects, or renderer dependencies.</summary>
public sealed record SectionStyleSnapshot(
    bool HasStyle, PatternDefinition? Pattern, double Scale, double Rotation,
    uint PatternColour, uint PatternPrintColour, BackgroundMode Background,
    uint BackgroundColour, uint BackgroundPrintColour, BoundaryDefinition Boundary,
    uint ObjectColour, uint ObjectPrintColour, Point2 PatternOrigin)
{
    public static SectionStyleSnapshot Default(uint colour = 0xff000000, uint printColour = 0xff000000) =>
        new(false, null, 1, 0, colour, printColour, BackgroundMode.None, 0, 0,
            new(true, 1, 0xff000000, 0xff000000, "Continuous", Array.Empty<double>()), colour, printColour, default);
}

public static class SectionPolicy
{
    // Rhino's unset default is not Color.Empty: preserve the raw input in the snapshot.
    public static uint ResolveColour(uint raw, uint fallback) => raw is 0 or 0x00ffffff ? fallback : raw;

    public static SectionStyleSnapshot EffectiveStyle(SectionStyleSnapshot style, HatchMode mode)
    {
        var plain = SectionStyleSnapshot.Default(style.ObjectColour, style.ObjectPrintColour);
        if (mode == HatchMode.None) return plain with { HasStyle = true };
        if (mode is HatchMode.Colour or HatchMode.Black || !style.HasStyle)
        {
            var colour = mode == HatchMode.Black ? 0xff000000 : style.ObjectColour;
            var print = mode == HatchMode.Black ? 0xff000000 : style.ObjectPrintColour;
            return plain with { HasStyle = true, Pattern = new("Solid", true, Array.Empty<PatternLine>()),
                PatternColour = colour, PatternPrintColour = print };
        }
        return style with {
            PatternColour = ResolveColour(style.PatternColour, style.ObjectColour),
            PatternPrintColour = ResolveColour(style.PatternPrintColour, style.ObjectPrintColour),
            Background = style.Background == BackgroundMode.Viewport ? BackgroundMode.SolidColour : style.Background,
            BackgroundColour = style.Background == BackgroundMode.Viewport ? style.ObjectColour : ResolveColour(style.BackgroundColour, style.ObjectColour),
            BackgroundPrintColour = style.Background == BackgroundMode.Viewport ? style.ObjectPrintColour : ResolveColour(style.BackgroundPrintColour, style.ObjectPrintColour),
            Boundary = style.Boundary with { Colour = ResolveColour(style.Boundary.Colour, 0xff000000),
                PrintColour = ResolveColour(style.Boundary.PrintColour, 0xff000000) } };
    }

    public static bool Compatible(SectionStyleSnapshot a, SectionStyleSnapshot b) =>
        a.HasStyle == b.HasStyle && PatternEqual(a.Pattern, b.Pattern) && a.Scale == b.Scale &&
        a.Rotation == b.Rotation && a.PatternOrigin == b.PatternOrigin && a.PatternColour == b.PatternColour &&
        a.PatternPrintColour == b.PatternPrintColour && a.Background == b.Background &&
        a.BackgroundColour == b.BackgroundColour && a.BackgroundPrintColour == b.BackgroundPrintColour &&
        a.Boundary.Visible == b.Boundary.Visible && a.Boundary.WidthScale == b.Boundary.WidthScale &&
        a.Boundary.Colour == b.Boundary.Colour && a.Boundary.PrintColour == b.Boundary.PrintColour &&
        a.Boundary.LinetypeName == b.Boundary.LinetypeName &&
        a.Boundary.LinetypeSegments.SequenceEqual(b.Boundary.LinetypeSegments);

    private static bool PatternEqual(PatternDefinition? a, PatternDefinition? b) =>
        a is null ? b is null : b is not null && a.Solid == b.Solid && a.Lines.Count == b.Lines.Count &&
        a.Lines.Zip(b.Lines).All(p => p.First.Angle == p.Second.Angle && p.First.Base == p.Second.Base &&
            p.First.Offset == p.Second.Offset && p.First.Dashes.SequenceEqual(p.Second.Dashes));

    public static SectionStatus Classify(double minimum, double maximum, double tolerance, bool exactExtrema, double roundoff)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum ||
            !double.IsFinite(tolerance) || tolerance <= 0 || !double.IsFinite(roundoff) || roundoff < 0)
            throw new ArgumentException("Invalid signed range or tolerance.");
        if (roundoff >= tolerance) return SectionStatus.Uncertain;
        if (minimum > tolerance + roundoff) return SectionStatus.InFront;
        if (maximum < -tolerance - roundoff) return SectionStatus.Behind;
        if (minimum < -tolerance - roundoff && maximum > tolerance + roundoff) return SectionStatus.Crossing;
        if (exactExtrema && (Math.Abs(minimum) <= roundoff && maximum > tolerance ||
            Math.Abs(maximum) <= roundoff && minimum < -tolerance ||
            Math.Abs(minimum) <= roundoff && Math.Abs(maximum) <= roundoff)) return SectionStatus.Contact;
        return SectionStatus.Uncertain;
    }

    public static string MembershipKey(IEnumerable<string> ids) => string.Join("|", ids.Order(StringComparer.Ordinal)
        .Select(s => $"{s.Length}:{s}"));
}

/// <summary>Containment forest from certified pair relationships; odd depth is void, even depth material.</summary>
public static class RegionTopology
{
    public sealed record Loop(int Index, int? Parent, int Depth, bool IsHole);
    public static IReadOnlyList<Loop> Nest(IReadOnlyList<double> areas, Func<int, int, bool> contains)
    {
        if (areas.Any(a => !double.IsFinite(a) || a <= 0)) throw new ArgumentException("Loops must have positive area.");
        var parents = new int?[areas.Count];
        for (var i = 0; i < areas.Count; i++)
        {
            var candidates = Enumerable.Range(0, areas.Count).Where(j => j != i && contains(j, i)).ToArray();
            if (candidates.Any(j => areas[j] <= areas[i])) throw new ArgumentException("Invalid containment relation.");
            parents[i] = candidates.OrderBy(j => areas[j]).Select(j => (int?)j).FirstOrDefault();
        }
        return Enumerable.Range(0, areas.Count).Select(i => {
            var depth = 0;
            for (var p = parents[i]; p.HasValue; p = parents[p.Value])
                if (++depth > areas.Count) throw new ArgumentException("Containment cycle.");
            return new Loop(i, parents[i], depth, depth % 2 != 0);
        }).ToArray();
    }
}
