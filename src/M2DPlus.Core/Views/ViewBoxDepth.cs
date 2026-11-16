using System.Globalization;
using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Views;

/// <summary>What a new view box shows, as Auto depth decides it (owner decision 2026-11-16, UX §2.2).</summary>
public enum ViewBoxKind
{
    /// <summary>A horizontal frame looking down whose front face cuts the model.</summary>
    Plan,
    /// <summary>Any other frame (vertical, tilted, or horizontal looking up) whose front face cuts the model.</summary>
    Section,
    /// <summary>A frame that cuts nothing: an elevation, roof plan or axo.</summary>
    Elevation,
}

/// <summary>Which Auto rule produced a depth.</summary>
public enum AutoDepthRule
{
    /// <summary>A plan: one storey (<see cref="ViewBoxDepth.StoreyMetres"/>) below the cut.</summary>
    Storey,
    /// <summary>A plan with nothing within one storey: to the back of what is inside the frame, never less than a storey.</summary>
    StoreyExtended,
    /// <summary>A section or elevation: to the back of what is inside the frame, plus a margin.</summary>
    Back,
    /// <summary>Nothing inside the frame: the frame's longer side.</summary>
    FrameSide,
}

/// <summary>One scene object as Auto depth sees it: its axis-aligned bounds, and whether the component can cut it
/// (surfaces, polysurfaces, extrusions, SubDs, meshes, blocks; not curves, points or annotations).</summary>
public readonly record struct DepthItem(Bounds3 Bounds, bool Cuttable);

/// <summary>The Auto depth of one view box, in document units, with the view kind and rule that produced it.
/// <see cref="Inside"/> counts the scene objects inside the frame's extrusion.</summary>
public sealed record AutoDepth(double Depth, ViewBoxKind Kind, AutoDepthRule Rule, int Inside)
{
    /// <summary>For messages: "plan, one storey below the cut".</summary>
    public string Describe() => Rule switch
    {
        AutoDepthRule.Storey => "plan, one storey below the cut",
        AutoDepthRule.StoreyExtended => "plan, nothing within one storey, so to the back of what's inside the frame",
        AutoDepthRule.Back => $"{(Kind == ViewBoxKind.Section ? "section" : "elevation")}, to the back of what's inside the frame",
        _ => "nothing inside the frame, so the frame's longer side",
    };
}

/// <summary>The depth a conversion uses: <see cref="IsAuto"/> (each box its own <see cref="AutoDepth"/>), or one explicit
/// value in document units for every box in the run.</summary>
public readonly record struct DepthChoice
{
    private DepthChoice(double? value) => Value = value;

    /// <summary>The explicit depth in document units, or null for Auto.</summary>
    public double? Value { get; }
    public bool IsAuto => Value is null;
    public static DepthChoice Auto => default;

    public static DepthChoice Of(double value) => double.IsFinite(value) && value > 0
        ? new(value) : throw new ArgumentOutOfRangeException(nameof(value), "Depth must be a positive finite distance.");

    /// <summary>The depth for one box: the explicit value, or its Auto depth.</summary>
    public double Resolve(AutoDepth auto) => Value ?? auto.Depth;

    /// <summary>Reads depth text as the settings parser does (a bare number is document units; <c>8 m</c>,
    /// <c>3000 mm</c>, <c>10'</c> carry their unit), or <c>auto</c>. The depth must be more than the document
    /// tolerance. False with a one-line error the dialog shows inline.</summary>
    public static bool TryParse(string? text, double metresPerDocumentUnit, double tolerance, out DepthChoice choice, out string? error)
    {
        choice = Auto; error = null;
        var value = text?.Trim() ?? "";
        if (value.Length == 0) { error = "Enter a depth, such as 8 m or 3000 mm, or press Auto."; return false; }
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        double depth;
        try { depth = Settings.ParseDistance(value, metresPerDocumentUnit); }
        catch (FormatException e)
        {
            // One short line: the dialog never wraps it.
            error = e.Message.StartsWith("Unknown distance unit:", StringComparison.Ordinal)
                ? $"{e.Message.Split(". ")[0].TrimEnd('.')}. Use mm, cm, m, km, in or ft."
                : $"'{value}' is not a distance. Try 8 m, 3000 mm or 10'.";
            return false;
        }
        if (!(depth > tolerance))
        {
            error = depth <= 0 ? "The depth must be greater than 0." : $"The depth must be more than the tolerance ({Format(tolerance)}).";
            return false;
        }
        choice = new(depth);
        return true;
    }

    internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Asks for the depth of one conversion run (the "View box depth" dialog), or null to cancel the run.</summary>
public delegate DepthChoice? DepthPrompt(ViewDepthPrompt request);

/// <summary>
/// Auto depth for new view boxes by view type (owner decision 2026-11-16, UX §2.2). Pure rules on plain boxes: the
/// host collects the bounds of what document mode draws (visible, never view boxes or M2D+ bakes) and narrows them
/// with a spatial index (<see cref="SearchBounds"/>); this decides.
/// <list type="bullet">
/// <item><b>Inside the frame</b>: an object whose bounds, projected onto the frame plane, overlap the frame rectangle by
/// more than the tolerance and that reaches behind the plane by more than the tolerance (in front of the frame is
/// removed by the drawing anyway).</item>
/// <item><b>Cuts</b>: as the front-face colour hint (<see cref="CutHint"/>), a cuttable object's bounds straddle the
/// plane and overlap the frame.</item>
/// <item><b>Plan</b> (horizontal, looking down, cuts): one storey, 3 m in document units. If nothing that is not cut
/// lies within it, to the back of what is inside the frame plus a margin, never less than a storey.</item>
/// <item><b>Section</b> (cuts, any other frame) and <b>elevation</b> (cuts nothing): to the back of what is inside the
/// frame, plus a margin (1 % of the diagonal of what is inside, at least 10 × the tolerance).</item>
/// <item><b>Nothing inside</b>: the frame's longer side.</item>
/// </list>
/// </summary>
public static class ViewBoxDepth
{
    /// <summary>One storey, the physical default plan depth.</summary>
    public const double StoreyMetres = 3;

    /// <summary>A frame whose normal is within this cosine of world Z is horizontal (as ViewBoxes.Face).</summary>
    public const double HorizontalCosine = .9;

    public static double Storey(double metresPerDocumentUnit) => StoreyMetres / Checked(metresPerDocumentUnit);

    /// <summary>A horizontal frame looking down: its Z (toward the viewer) points up.</summary>
    public static bool LooksDown(Plane3 plane) => plane.Z.Z >= HorizontalCosine;

    public static AutoDepth Auto(FrontFace face, IEnumerable<DepthItem> items, double tolerance, double metresPerDocumentUnit)
    {
        var storey = Storey(metresPerDocumentUnit);
        var plane = face.Plane;
        var count = 0;
        var back = double.NegativeInfinity;
        var cuts = false;
        var withinStorey = false;
        Bounds3? union = null;
        foreach (var item in items)
        {
            var box = item.Bounds;
            var centre = (box.Min + box.Max) * .5;
            var half = (box.Max - box.Min) * .5;
            var local = centre - plane.Origin;
            var w = local.Dot(plane.Z); var rw = Reach(half, plane.Z);
            if (!(w - rw < -tolerance)) continue; // nothing behind the frame plane
            var u = local.Dot(plane.X); var ru = Reach(half, plane.X);
            if (!(u - ru < face.Width - tolerance && u + ru > tolerance)) continue;
            var v = local.Dot(plane.Y); var rv = Reach(half, plane.Y);
            if (!(v - rv < face.Height - tolerance && v + rv > tolerance)) continue;
            count++;
            back = Math.Max(back, rw - w);
            var straddles = w + rw > tolerance;
            if (straddles && item.Cuttable && CutHint.Cuts(face, box, tolerance)) cuts = true;
            if (!straddles && -(w + rw) < storey) withinStorey = true;
            union = union is { } u0
                ? new Bounds3(new(Math.Min(u0.Min.X, box.Min.X), Math.Min(u0.Min.Y, box.Min.Y), Math.Min(u0.Min.Z, box.Min.Z)),
                    new(Math.Max(u0.Max.X, box.Max.X), Math.Max(u0.Max.Y, box.Max.Y), Math.Max(u0.Max.Z, box.Max.Z)))
                : box;
        }
        var kind = !cuts ? ViewBoxKind.Elevation : LooksDown(plane) ? ViewBoxKind.Plan : ViewBoxKind.Section;
        if (union is not { } all) return new(Math.Max(face.Width, face.Height), kind, AutoDepthRule.FrameSide, 0);
        var margin = Math.Max(tolerance * 10, (all.Max - all.Min).Length * .01);
        if (kind == ViewBoxKind.Plan)
            return withinStorey ? new(storey, kind, AutoDepthRule.Storey, count)
                : new(Math.Max(storey, back + margin), kind, AutoDepthRule.StoreyExtended, count);
        return new(back + margin, kind, AutoDepthRule.Back, count);
    }

    /// <summary>The axis-aligned box around the frame swept back along the look direction to the far side of
    /// <paramref name="scene"/>: every object that can be inside the frame meets it, so it is the spatial-index query.
    /// Null when nothing of the scene lies behind the frame plane.</summary>
    public static Bounds3? SearchBounds(FrontFace face, Bounds3 scene, double tolerance)
    {
        var plane = face.Plane;
        var reach = Corners(scene).Max(c => -(c - plane.Origin).Dot(plane.Z));
        if (!(reach > tolerance)) return null;
        var points = new List<Vector3>(8);
        foreach (var (u, v) in new[] { (0d, 0d), (face.Width, 0d), (face.Width, face.Height), (0d, face.Height) })
        {
            points.Add(plane.At(u, v));
            points.Add(plane.At(u, v, -reach));
        }
        var grow = new Vector3(tolerance, tolerance, tolerance);
        return new(new Vector3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)) - grow,
            new Vector3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)) + grow);
    }

    /// <summary>The short unit name the settings parser reads for a document unit (mm, cm, m, km, in, ft), or null
    /// for other units (a bare number is then document units).</summary>
    public static string? UnitSuffix(double metresPerDocumentUnit)
    {
        foreach (var (metres, name) in new[] { (.001, "mm"), (.01, "cm"), (1d, "m"), (1000d, "km"), (.0254, "in"), (.3048, "ft") })
            if (Math.Abs(metresPerDocumentUnit / metres - 1) < 1e-9) return name;
        return null;
    }

    /// <summary>A depth in document units as the dialog shows it: <c>3000 mm</c>, <c>12.437 m</c>.</summary>
    public static string FormatDepth(double depth, double metresPerDocumentUnit) =>
        UnitSuffix(metresPerDocumentUnit) is { } suffix ? $"{DepthChoice.Format(depth)} {suffix}" : DepthChoice.Format(depth);

    private static double Reach(Vector3 half, Vector3 axis) => Math.Abs(half.X * axis.X) + Math.Abs(half.Y * axis.Y) + Math.Abs(half.Z * axis.Z);
    private static double Checked(double metresPerDocumentUnit) => double.IsFinite(metresPerDocumentUnit) && metresPerDocumentUnit > 0
        ? metresPerDocumentUnit : throw new ArgumentOutOfRangeException(nameof(metresPerDocumentUnit));

    private static IEnumerable<Vector3> Corners(Bounds3 b)
    {
        foreach (var x in new[] { b.Min.X, b.Max.X })
        foreach (var y in new[] { b.Min.Y, b.Max.Y })
        foreach (var z in new[] { b.Min.Z, b.Max.Z })
            yield return new(x, y, z);
    }
}

/// <summary>
/// The model of the "View box depth" dialog for one conversion run (owner decision 2026-11-16, UX §2.2): one depth for
/// every box the run converts. <see cref="AutoText"/> is what the Auto button fills in (the Auto value when every box
/// agrees, else <c>auto</c>), <see cref="Hint"/> the one line under the box saying what Auto means for this selection.
/// Every text is one short line: the dialog never wraps.
/// Text equal to <see cref="AutoText"/> or <c>auto</c> keeps Auto, so each box gets its own Auto depth.
/// </summary>
public sealed class ViewDepthPrompt
{
    public ViewDepthPrompt(IReadOnlyList<AutoDepth> autos, double metresPerDocumentUnit, double tolerance)
    {
        if (autos.Count == 0) throw new ArgumentException("A depth prompt needs at least one view box.", nameof(autos));
        Autos = autos; MetresPerDocumentUnit = metresPerDocumentUnit; Tolerance = tolerance;
        var first = autos[0].Depth;
        AutoText = autos.All(a => Math.Abs(a.Depth - first) <= Math.Max(tolerance, 1e-9))
            ? ViewBoxDepth.FormatDepth(first, metresPerDocumentUnit) : "auto";
        Hint = HintFor(autos);
    }

    public IReadOnlyList<AutoDepth> Autos { get; }
    public double MetresPerDocumentUnit { get; }
    public double Tolerance { get; }
    public string Title => "View box depth";
    public string AutoText { get; }
    public string Hint { get; }

    /// <summary>The label in front of the depth box: "Depth", or "Depth for all 3" when the run makes several boxes.</summary>
    public string DepthLabel => Autos.Count > 1 ? $"Depth for all {Autos.Count}" : "Depth";

    /// <summary>OK: Auto for <see cref="AutoText"/> or <c>auto</c>, else the parsed depth, or false with the inline error.</summary>
    public bool TryAccept(string? text, out DepthChoice choice, out string? error)
    {
        if (string.Equals(text?.Trim(), AutoText, StringComparison.OrdinalIgnoreCase)) { choice = DepthChoice.Auto; error = null; return true; }
        return DepthChoice.TryParse(text, MetresPerDocumentUnit, Tolerance, out choice, out error);
    }

    /// <summary>"Auto: plans 3 m below the cut; sections and elevations to the back of what's inside the frame."</summary>
    public static string HintFor(IReadOnlyCollection<AutoDepth> autos)
    {
        var storey = ViewBoxDepth.FormatDepth(ViewBoxDepth.StoreyMetres, 1);
        var parts = new List<string>();
        if (autos.Any(a => a.Rule == AutoDepthRule.Storey))
            parts.Add(autos.Any(a => a.Rule == AutoDepthRule.StoreyExtended)
                ? $"plans {storey} below the cut, more if nothing is there" : $"plans {storey} below the cut");
        else if (autos.Any(a => a.Rule == AutoDepthRule.StoreyExtended))
            parts.Add($"plans to the back of what's inside the frame (nothing within {storey})");
        var section = autos.Any(a => a.Rule == AutoDepthRule.Back && a.Kind == ViewBoxKind.Section);
        var elevation = autos.Any(a => a.Rule == AutoDepthRule.Back && a.Kind == ViewBoxKind.Elevation);
        if (section || elevation)
            parts.Add($"{(section && elevation ? "sections and elevations" : section ? "sections" : "elevations")} to the back of what's inside the frame");
        if (autos.Any(a => a.Rule == AutoDepthRule.FrameSide))
            parts.Add(parts.Count == 0 ? "nothing is inside the frame, so its longer side" : "empty frames their longer side");
        return "Auto: " + string.Join("; ", parts) + ".";
    }
}
