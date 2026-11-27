using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>The material planes used by section certification and Route C, in the geometry's frame.</summary>
internal sealed record SectionValidation(Plane Near, Plane? Far, double Margin)
{
    internal const string Policy = "near-cut-intersection-portions-v1";

    // The stability certificate samples +/-4 tolerances. Include the native
    // intersection tolerance as well; roundoff is bound to the parent validation copy.
    internal static SectionValidation For(SectionRequest request, Plane? far = null) =>
        new(request.Plane, far, (StableSectionCertificate.PerturbationTolerances + 1) * request.Tolerance);

    internal SectionValidation Transform(Transform transform)
    {
        var near = Near;
        var far = Far;
        if (!near.Transform(transform)) throw new SectionFailure("Cannot transform section validation plane.");
        if (far is { } plane)
        {
            if (!plane.Transform(transform)) throw new SectionFailure("Cannot transform section validation plane.");
            far = plane;
        }
        // All callers use rigid transforms: canonical geometry is already scaled
        // to instance units, so the margin and tolerance stay in those same units.
        return new(near, far, Margin);
    }

    internal string Key => FormattableString.Invariant($"{Policy}|margin={Margin:R}|near={PlaneKey(Near)}|far={(Far is { } far ? PlaneKey(far) : "none")}");

    // Bind once to the entire validation copy, never to a progressively smaller
    // proof node. The same closed domain drives broad phase, proof and sections.
    private BoundingBox ParentBounds { get; init; } = BoundingBox.Unset;
    private (Plane Near, Plane? Far, double Margin, Slab[] Slabs)? BoundDomains { get; init; }
    internal SectionValidation WithBounds(BoundingBox bounds)
    {
        var bound = this with { ParentBounds = bounds, BoundDomains = null };
        return bound with { BoundDomains = (Near, Far, Margin, bound.Domains(bounds)) };
    }

    internal readonly record struct Slab(Plane Frame, double HalfWidth, double Roundoff)
    {
        internal Plane Boundary(int sign) => new(Frame.Origin + sign * HalfWidth * Frame.Normal, Frame.XAxis, Frame.YAxis);
    }

    internal Slab[] Domains(BoundingBox fallback)
    {
        if (BoundDomains is { } cached && cached.Near == Near && cached.Far == Far && cached.Margin == Margin) return cached.Slabs;
        var bounds = ParentBounds.IsValid ? ParentBounds : fallback;
        Slab Make(Plane plane)
        {
            var n = plane.Normal;
            var offset = Vector3d.Multiply(n, (Vector3d)plane.Origin);
            // Keys contain only this equation. Canonical axes and origin therefore
            // cannot inherit authored in-plane choices (including survey origins).
            var axis = Math.Abs(n.X) <= Math.Abs(n.Y) && Math.Abs(n.X) <= Math.Abs(n.Z) ? Vector3d.XAxis :
                Math.Abs(n.Y) <= Math.Abs(n.Z) ? Vector3d.YAxis : Vector3d.ZAxis;
            var x = Vector3d.CrossProduct(axis, n); x.Unitize();
            var frame = new Plane((Point3d)(offset * n), x, Vector3d.CrossProduct(n, x));
            var magnitude = Math.Max(1, Math.Max(Math.Abs(offset),
                Math.Max(bounds.Min.MaximumCoordinate, bounds.Max.MaximumCoordinate)));
            // Covers source-coordinate dot products, canonical origin/axes and
            // world-to-plane arithmetic, over the parent geometry, not this box.
            var rho = 1024 * 2.2204460492503131e-16 * magnitude;
            return new(frame, Margin + rho, rho);
        }
        return Far is { } far ? [Make(Near), Make(far)] : [Make(Near)];
    }

    private bool Valid => Near.IsValid && (Far is null || Far.Value.IsValid) && double.IsFinite(Margin) && Margin >= 0;
    private static bool Finite(BoundingBox box) => box.IsValid && double.IsFinite(box.Min.X) && double.IsFinite(box.Min.Y) &&
        double.IsFinite(box.Min.Z) && double.IsFinite(box.Max.X) && double.IsFinite(box.Max.Y) && double.IsFinite(box.Max.Z);

    // World boxes remain useful for points and the broad phase. Curves use their
    // own tight plane-frame box; the world box is retained by the topology proof.
    internal bool Excludes(BoundingBox box) => Valid && Finite(box) && Domains(box).All(s => Outside(Range(box, s.Frame), s.HalfWidth));
    // Containment is only a conservative early-refusal shortcut: inset the
    // closed domain by two arithmetic pads rather than enlarging the proof.
    internal bool Contains(BoundingBox box) => Valid && Finite(box) && Domains(box).Any(s => Inside(Range(box, s.Frame), s.HalfWidth - 2*s.Roundoff));
    internal bool Excludes(Curve curve) => TestCurve(curve, false);
    internal bool Contains(Curve curve) => TestCurve(curve, true);
    private bool TestCurve(Curve curve, bool contain)
    {
        if (!Valid) return false;
        var bounds = curve.GetBoundingBox(true);
        if (!Finite(bounds)) return false;
        var results = Domains(bounds).Select(s =>
        {
            var box = curve.GetBoundingBox(s.Frame);
            return Finite(box) && (contain ? Inside((box.Min.Z, box.Max.Z), s.HalfWidth - 2*s.Roundoff) : Outside((box.Min.Z, box.Max.Z), s.HalfWidth));
        });
        return contain ? results.Any(v => v) : results.All(v => v);
    }
    private static bool Outside((double Min, double Max) range, double width) => range.Min > width || range.Max < -width;
    private static bool Inside((double Min, double Max) range, double width) => range.Min > -width && range.Max < width;
    private static (double Min, double Max) Range(BoundingBox box, Plane plane)
    {
        var n = plane.Normal;
        var offset = Vector3d.Multiply(n, (Vector3d)plane.Origin);
        return (n.X * (n.X >= 0 ? box.Min.X : box.Max.X) + n.Y * (n.Y >= 0 ? box.Min.Y : box.Max.Y) +
            n.Z * (n.Z >= 0 ? box.Min.Z : box.Max.Z) - offset,
            n.X * (n.X >= 0 ? box.Max.X : box.Min.X) + n.Y * (n.Y >= 0 ? box.Max.Y : box.Min.Y) +
            n.Z * (n.Z >= 0 ? box.Max.Z : box.Min.Z) - offset);
    }

    private static string PlaneKey(Plane plane) => FormattableString.Invariant(
        $"{plane.Normal.X:R},{plane.Normal.Y:R},{plane.Normal.Z:R},{-Vector3d.Multiply(plane.Normal, (Vector3d)plane.Origin):R}");

    internal int Touches(BoundingBox box)
    {
        if (!Valid || !Finite(box)) return 3;
        var slabs = Domains(box);
        return (Outside(Range(box, slabs[0].Frame), slabs[0].HalfWidth) ? 0 : 1) |
            (slabs.Length == 2 && !Outside(Range(box, slabs[1].Frame), slabs[1].HalfWidth) ? 2 : 0);
    }
}
