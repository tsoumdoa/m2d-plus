using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>The material planes used by section certification and Route C, in the geometry's frame.</summary>
internal sealed record SectionValidation(Plane Near, Plane? Far, double Margin)
{
    internal const string Policy = "near-cut-intersection-portions-v1";

    // The stability certificate samples +/-4 tolerances. Include the native
    // intersection tolerance as well; roundoff is added conservatively at each box.
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

    // Only an enclosing box strictly outside BOTH expanded slabs can be omitted.
    // Boundaries (including roundoff) and invalid values always remain relevant.
    internal bool Excludes(BoundingBox box) => Near.IsValid && (Far is null || Far.Value.IsValid) &&
        double.IsFinite(Margin) && Margin >= 0 && box.IsValid && Finite(box) &&
        Separated(box, Near) && (Far is not { } far || Separated(box, far));

    internal bool Contains(BoundingBox box)
    {
        if (!box.IsValid || !Finite(box)) return false;
        bool Inside(Plane plane)
        {
            var (min, max, roundoff) = Range(box, plane);
            return min > -Margin + roundoff && max < Margin - roundoff;
        }
        return Inside(Near) || Far is { } far && Inside(far);
    }

    private static bool Finite(BoundingBox box) => double.IsFinite(box.Min.X) && double.IsFinite(box.Min.Y) &&
        double.IsFinite(box.Min.Z) && double.IsFinite(box.Max.X) && double.IsFinite(box.Max.Y) && double.IsFinite(box.Max.Z);

    private bool Separated(BoundingBox box, Plane plane)
    {
        var (min, max, roundoff) = Range(box, plane);
        return double.IsFinite(min) && double.IsFinite(max) &&
            (min > Margin + roundoff || max < -Margin - roundoff);
    }

    // Exact extrema of a linear functional on an axis-aligned enclosing box,
    // with the same coordinate-scaled margin as the face broad phase. Avoid
    // allocating eight corners at every continuous-proof subdivision node.
    private static (double Min, double Max, double Roundoff) Range(BoundingBox box, Plane plane)
    {
        var n = plane.Normal;
        var offset = Vector3d.Multiply(n, (Vector3d)plane.Origin);
        var min = n.X * (n.X >= 0 ? box.Min.X : box.Max.X) + n.Y * (n.Y >= 0 ? box.Min.Y : box.Max.Y) +
            n.Z * (n.Z >= 0 ? box.Min.Z : box.Max.Z) - offset;
        var max = n.X * (n.X >= 0 ? box.Max.X : box.Min.X) + n.Y * (n.Y >= 0 ? box.Max.Y : box.Min.Y) +
            n.Z * (n.Z >= 0 ? box.Max.Z : box.Min.Z) - offset;
        var magnitude = Math.Max(1, Math.Max(Math.Abs(offset), Math.Max(box.Min.MaximumCoordinate, box.Max.MaximumCoordinate)));
        return (min, max, 256 * 2.2204460492503131e-16 * magnitude);
    }

    private static string PlaneKey(Plane plane) => FormattableString.Invariant(
        $"{plane.Normal.X:R},{plane.Normal.Y:R},{plane.Normal.Z:R},{-Vector3d.Multiply(plane.Normal, (Vector3d)plane.Origin):R}");

    internal int Touches(BoundingBox box)
    {
        if (!box.IsValid) return 3; // An unprovable bound never excludes a face.
        var corners = box.GetCorners();
        bool TouchesPlane(Plane plane)
        {
            // Use the same equation as Key, independent of the arbitrary
            // in-plane origin. Its roundoff margin must also depend only on
            // that equation and the geometry, so equal keys have equal tests.
            var offset = Vector3d.Multiply(plane.Normal, (Vector3d)plane.Origin);
            var distances = corners.Select(p => Vector3d.Multiply(plane.Normal, (Vector3d)p) - offset).ToArray();
            var magnitude = Math.Max(1, Math.Max(Math.Abs(offset),
                Math.Max(box.Min.MaximumCoordinate, box.Max.MaximumCoordinate)));
            var margin = Margin + 256 * 2.2204460492503131e-16 * magnitude;
            return distances.Min() <= margin && distances.Max() >= -margin;
        }
        return (TouchesPlane(Near) ? 1 : 0) | (Far is { } far && TouchesPlane(far) ? 2 : 0);
    }
}
