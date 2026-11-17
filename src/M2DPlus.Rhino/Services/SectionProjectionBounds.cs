using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal enum SectionProjectionRelation { Section, Projection, Excluded }

/// <summary>Conservative halfspace separation only; never a section or visibility certificate.</summary>
internal static class SectionProjectionBounds
{
    internal static SectionProjectionRelation Classify(GeometryBase geometry, Plane near, Plane? far, double tolerance)
    {
        // The non-tight geometry box encloses the complete source, including all
        // disconnected shells and curved faces. Linear plane distance attains its
        // extrema at its corners. Reserve both modelling tolerance and arithmetic
        // error; a contact or uncertain bound never bypasses section validation.
        var bounds = geometry.GetBoundingBox(false);
        if (!bounds.IsValid) return SectionProjectionRelation.Section;
        var corners = bounds.GetCorners();
        int Side(Plane plane)
        {
            var distances = corners.Select(plane.DistanceTo).ToArray();
            double magnitude = corners.Max(p => Math.Max(p.DistanceTo(Point3d.Origin), plane.Origin.DistanceTo(Point3d.Origin)));
            double margin = ViewSourceCull.Margin(tolerance, magnitude);
            if (distances.All(d => d > margin)) return 1;
            return distances.All(d => d < -margin) ? -1 : 0;
        }
        int nearSide = Side(near), farSide = far is { } plane ? Side(plane) : -1;
        if (nearSide > 0 || farSide > 0) return SectionProjectionRelation.Excluded;
        return nearSide < 0 && farSide < 0 ? SectionProjectionRelation.Projection : SectionProjectionRelation.Section;
    }
}
