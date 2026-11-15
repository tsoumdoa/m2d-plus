using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Views;

/// <summary>An sRGB colour with alpha (0 = clear, 255 = opaque).</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public double Opacity => A / 255d;
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
    public override string ToString() => Hex;
}

/// <summary>Where a view box's front-face fill colour came from.</summary>
public enum FillSource
{
    /// <summary>By type: red when the front face cuts the model, cobalt when it doesn't.</summary>
    ByType,
    /// <summary>The view's own <c>m2d.color</c>.</summary>
    Override,
    /// <summary><c>m2d.color</c> is not a colour; the fill falls back to by type and the component remarks it.</summary>
    Invalid,
}

/// <summary>The fill drawn on one view box's front face.</summary>
public sealed record FillChoice(Rgba Colour, bool Cuts, FillSource Source, string? Problem);

/// <summary>
/// The see-through fill M2D+ draws on each view box's front face (owner decision 2026-11-15, UX §2.2):
/// warm red when the front face cuts the model (a section or plan), cobalt when it doesn't (an elevation or axo),
/// or the view's <c>m2d.color</c> override. Pure rules; the Rhino display conduit only draws the result.
/// </summary>
public static class ViewBoxFill
{
    public const string Key = "m2d.color", Inherit = "default";

    /// <summary>Section / plan: warm red #D9412B at 25 % opacity.</summary>
    public static Rgba Cut { get; } = new(0xD9, 0x41, 0x2B, 0x40);

    /// <summary>Elevation / axo: cobalt #1F6FD1 at 22 % opacity (these faces usually sit in front of the whole model,
    /// so they are a little lighter).</summary>
    public static Rgba NoCut { get; } = new(0x1F, 0x6F, 0xD1, 0x38);

    public static Rgba ByType(bool cuts) => cuts ? Cut : NoCut;

    /// <summary>Parses an <c>m2d.color</c> value. Blank or <c>default</c> (any case) means by type: true with a null
    /// colour. <c>#RRGGBB</c> keeps the by-type opacity (alpha null); <c>#RRGGBBAA</c> sets it. Hex digits in any case,
    /// surrounding spaces ignored. Anything else is false.</summary>
    public static bool TryParse(string? text, out byte? r, out byte? g, out byte? b, out byte? a)
    {
        r = g = b = a = null;
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Equals(Inherit, StringComparison.OrdinalIgnoreCase)) return true;
        if (value[0] != '#' || value.Length is not (7 or 9)) return false;
        var bytes = new byte[(value.Length - 1) / 2];
        for (var i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(value.AsSpan(1 + 2 * i, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i])) return false;
        (r, g, b) = (bytes[0], bytes[1], bytes[2]);
        if (bytes.Length == 4) a = bytes[3];
        return true;
    }

    /// <summary>The fill for a view whose front face does (<paramref name="cuts"/>) or doesn't cut the model, given its
    /// <c>m2d.color</c> user text (null when the key is missing). <paramref name="view"/> names it in the problem.</summary>
    public static FillChoice Choose(bool cuts, string? userText, string view = "this view")
    {
        var byType = ByType(cuts);
        if (!TryParse(userText, out var r, out var g, out var b, out var a))
            return new(byType, cuts, FillSource.Invalid, Problem(userText!, view));
        return r is null ? new(byType, cuts, FillSource.ByType, null)
            : new(new(r.Value, g!.Value, b!.Value, a ?? byType.A), cuts, FillSource.Override, null);
    }

    /// <summary>The component remark for an invalid <c>m2d.color</c>, or null when the value is fine.</summary>
    public static string? Validate(string? userText, string view) =>
        TryParse(userText, out _, out _, out _, out _) ? null : Problem(userText!, view);

    private static string Problem(string text, string view) =>
        $"{view}: m2d.color = '{text.Trim()}' is not a colour. Use #RRGGBB, #RRGGBBAA or default; its front face is coloured by type.";
}

/// <summary>An axis-aligned box in model coordinates.</summary>
public readonly record struct Bounds3(Vector3 Min, Vector3 Max);

/// <summary>A view box's front face: the rectangle [0, Width] × [0, Height] in <paramref name="Plane"/>, whose origin
/// is the lower-left corner and whose Z points at the viewer (the view looks along −Z).</summary>
public sealed class FrontFace(Plane3 plane, double width, double height)
{
    public Plane3 Plane { get; } = plane;
    public double Width { get; } = width;
    public double Height { get; } = height;

    /// <summary>The axis-aligned box around the frame rectangle.</summary>
    public Bounds3 Extent { get; } = ExtentOf(plane, width, height);

    private static Bounds3 ExtentOf(Plane3 p, double width, double height)
    {
        Vector3[] corners = [p.At(0, 0), p.At(width, 0), p.At(width, height), p.At(0, height)];
        return new(new(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
            new(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)));
    }
}

/// <summary>
/// The display hint "does this front face cut the model?" for colouring only (UX §2.2). It is cheap and conservative
/// on plain boxes: an object counts when its bounding box <b>straddles</b> the front-face plane by more than the
/// tolerance on both sides (as the component's cut rule, which never cuts a face lying on the plane), its bounding
/// box, projected onto the plane, <b>overlaps the frame rectangle</b> by more than the tolerance in both directions,
/// and it <b>meets the frame's own bounding box</b> (grown by the tolerance; this is also the spatial-index query).
/// A real cut inside the frame always passes (no false negatives); a bounding box can pass without a real cut
/// (an L-shaped object, or a box near an inclined frame).
/// </summary>
public static class CutHint
{
    public static bool Cuts(FrontFace face, Bounds3 box, double tolerance)
    {
        var extent = face.Extent;
        if (box.Min.X > extent.Max.X + tolerance || box.Max.X < extent.Min.X - tolerance ||
            box.Min.Y > extent.Max.Y + tolerance || box.Max.Y < extent.Min.Y - tolerance ||
            box.Min.Z > extent.Max.Z + tolerance || box.Max.Z < extent.Min.Z - tolerance) return false;
        var plane = face.Plane;
        var centre = (box.Min + box.Max) * .5;
        var half = (box.Max - box.Min) * .5;
        var local = centre - plane.Origin;
        static double Reach(Vector3 half, Vector3 axis) => Math.Abs(half.X * axis.X) + Math.Abs(half.Y * axis.Y) + Math.Abs(half.Z * axis.Z);
        var w = local.Dot(plane.Z); var rw = Reach(half, plane.Z);
        if (!(w - rw < -tolerance && w + rw > tolerance)) return false;
        var u = local.Dot(plane.X); var ru = Reach(half, plane.X);
        if (!(u - ru < face.Width - tolerance && u + ru > tolerance)) return false;
        var v = local.Dot(plane.Y); var rv = Reach(half, plane.Y);
        return v - rv < face.Height - tolerance && v + rv > tolerance;
    }

    /// <summary>The axis-aligned box around the frame rectangle grown by the tolerance: every object that can pass
    /// <see cref="Cuts"/> intersects it, so it is the spatial-index query.</summary>
    public static Bounds3 SearchBounds(FrontFace face, double tolerance)
    {
        var grow = new Vector3(tolerance, tolerance, tolerance);
        return new(face.Extent.Min - grow, face.Extent.Max + grow);
    }

    /// <summary>Whether any of <paramref name="boxes"/> makes the face cut (the reference the spatial index must match).</summary>
    public static bool AnyCuts(FrontFace face, IEnumerable<Bounds3> boxes, double tolerance) => boxes.Any(b => Cuts(face, b, tolerance));
}
