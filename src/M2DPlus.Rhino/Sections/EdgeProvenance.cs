using M2DPlus.Core.Sections;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Sections;

internal static class EdgeProvenance
{
    /// <summary>Split at overlap endpoints, then label intervals. No whole-curve midpoint heuristic.</summary>
    public static IReadOnlyList<SectionEdge> Split(Curve curve, IEnumerable<(Curve Curve, EdgeOrigin Origin)> references,
        double tolerance, EdgeOrigin otherwise = EdgeOrigin.None)
    {
        var overlaps = new List<(Interval Interval, EdgeOrigin Origin)>();
        var parameters = new SortedSet<double> { curve.Domain.T0, curve.Domain.T1 };
        var bounds = curve.GetBoundingBox(true);
        foreach (var reference in references)
        {
            var other = reference.Curve.GetBoundingBox(true);
            if (bounds.Min.X > other.Max.X + tolerance || bounds.Max.X < other.Min.X - tolerance ||
                bounds.Min.Y > other.Max.Y + tolerance || bounds.Max.Y < other.Min.Y - tolerance ||
                bounds.Min.Z > other.Max.Z + tolerance || bounds.Max.Z < other.Min.Z - tolerance) continue;
            using var events = Intersection.CurveCurve(curve, reference.Curve, tolerance, tolerance);
            foreach (var item in events)
            {
                if (!item.IsOverlap) continue;
                var interval = item.OverlapA;
                overlaps.Add((interval, reference.Origin));
                parameters.Add(Math.Clamp(interval.Min, curve.Domain.Min, curve.Domain.Max));
                parameters.Add(Math.Clamp(interval.Max, curve.Domain.Min, curve.Domain.Max));
            }
        }
        var result = new List<SectionEdge>();
        var ends = parameters.ToArray();
        for (var i = 1; i < ends.Length; i++)
        {
            if (ends[i] <= ends[i - 1]) continue;
            var middle = (ends[i] + ends[i - 1]) / 2;
            var origin = overlaps.Where(o => o.Interval.IncludesParameter(middle)).Aggregate(EdgeOrigin.None, (a, o) => a | o.Origin);
            var piece = M2DPlus.Rhino.Services.CurveIntervals.Trim(curve, new(ends[i - 1], ends[i]));
            if (piece is null && curve is LineCurve && curve.PointAt(ends[i - 1]) == curve.PointAt(ends[i])) continue;
            if (piece is null) throw new SectionFailure("Provenance interval extraction failed.");
            result.Add(new(piece, origin == EdgeOrigin.None ? otherwise : origin));
        }
        return result;
    }
}
