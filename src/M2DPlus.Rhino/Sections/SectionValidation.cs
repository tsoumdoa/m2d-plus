using Rhino.Geometry;

namespace M2DPlus.Rhino.Sections;

/// <summary>The material planes used by section certification and Route C, in the geometry's frame.</summary>
internal sealed record SectionValidation(Plane Near, Plane? Far, double Margin)
{
    internal const string Policy = "near-cut-face-pairs-v1";

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
