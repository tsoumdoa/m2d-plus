using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Certificates for an entire stretch, never a vote at a few points. Positive rational NURBS lie in
/// their control hull: its radius about the sample bounds projected travel (and hence the 1-Lipschitz distance
/// to any fixed witness). Its maximum depth bounds the depth condition without assuming a continuous nearest
/// parameter on the covering curve; that parameter can jump at a fold or between two covering pieces.</summary>
internal static class BoundedCurveCover
{
    internal sealed record Piece(Curve World, Curve Flat, double Gap);

    internal static bool Covers(Curve world, Curve flat, Interval domain, IReadOnlyList<Piece> others,
        Plane plane, double tolerance, double gap, bool depth, CancellationToken token = default, int cap = 32768)
    {
        double Depth(Point3d p) => (p - plane.Origin) * plane.ZAxis;
        var stack = new Stack<Interval>(); stack.Push(domain); int samples = 0;
        while (stack.TryPop(out var interval))
        {
            token.ThrowIfCancellationRequested();
            if (++samples > cap) return false;
            using var part = world.Trim(interval); using var n = part?.ToNurbsCurve();
            if (n is null || n.Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight))) return false;
            // This also handles exact circles efficiently: identical bases let the difference curve be bounded by
            // the differences of corresponding control points, without millions of tolerance-spaced samples.
            if (others.Any(v => Parametric(n, v, plane, tolerance, gap, depth))) continue;
            double middle = interval.Mid; var at = flat.PointAt(middle);
            double travel = 0, highest = double.NegativeInfinity;
            foreach (var p in n.Points)
            {
                var projected = plane.ClosestPoint(p.Location);
                travel = Math.Max(travel, projected.DistanceTo(at)); highest = Math.Max(highest, Depth(p.Location));
            }
            double guard = 1e-12 * (1 + at.DistanceTo(Point3d.Origin) + travel);
            bool covered = false;
            foreach (var v in others)
            {
                double radius = tolerance / 2 + gap + v.Gap;
                if (!v.Flat.ClosestPoint(at, out double t, radius)) continue;
                if (at.DistanceTo(v.Flat.PointAt(t)) + travel + guard > radius) continue;
                // Keep this witness fixed on the whole interval. The depth of a moving closest point need not be
                // Lipschitz (especially on the far rim of a cope seen down the cutter's axis).
                if (depth && highest + guard > Depth(v.World.PointAt(t)) + tolerance + gap + v.Gap) continue;
                covered = true; break;
            }
            if (covered) continue;
            if (!(middle > interval.Min && middle < interval.Max)) return false;
            // A failed centre test already disproves coverage; there is no reason to refine it.
            if (!others.Any(v => v.Flat.ClosestPoint(at, out double t, tolerance / 2 + gap + v.Gap) &&
                (!depth || Depth(world.PointAt(middle)) <= Depth(v.World.PointAt(t)) + tolerance + gap + v.Gap))) return false;
            stack.Push(new(middle, interval.Max)); stack.Push(new(interval.Min, middle));
        }
        return true;
    }

    private static bool Parametric(NurbsCurve mine, Piece other, Plane plane, double tol, double gap, bool depth)
    {
        var a = plane.ClosestPoint(mine.PointAtStart); var b = plane.ClosestPoint(mine.PointAtEnd);
        double radius = tol / 2 + gap + other.Gap;
        if (!other.Flat.ClosestPoint(a, out double ta, radius) || !other.Flat.ClosestPoint(b, out double tb, radius) || ta == tb) return false;
        using var part = other.World.Trim(new Interval(Math.Min(ta, tb), Math.Max(ta, tb)));
        using var n = part?.ToNurbsCurve();
        if (n is null) return false;
        if (tb < ta) n.Reverse();
        if (mine.Degree != n.Degree || mine.Points.Count != n.Points.Count || mine.Knots.Count != n.Knots.Count) return false;
        // Equal normalised knots give the same polynomial basis. Bound the rational weight difference too:
        // ||alpha-beta||_1 <= 2 max|w-ratio*v| / min(w), where alpha,beta are the normalised rational bases.
        using var m = mine.ToNurbsCurve();
        m.Domain = new(0, 1); n.Domain = new(0, 1);
        for (int k = 0; k < m.Knots.Count; k++) if (m.Knots[k] != n.Knots[k]) return false;
        double ratio = m.Points[0].Weight / n.Points[0].Weight;
        double guard = 1e-12 * (1 + a.DistanceTo(Point3d.Origin));
        double weightError = 0, minWeight = double.PositiveInfinity, separation = 0, rise = double.NegativeInfinity;
        double extent = 0, depthExtent = 0; var anchor = n.Points[0].Location;
        for (int k = 0; k < m.Points.Count; k++)
        {
            var p = m.Points[k]; var q = n.Points[k];
            if (!(q.Weight > 0) || !double.IsFinite(q.Weight)) return false;
            weightError = Math.Max(weightError, Math.Abs(p.Weight - q.Weight * ratio)); minWeight = Math.Min(minWeight, p.Weight);
            separation = Math.Max(separation, plane.ClosestPoint(p.Location).DistanceTo(plane.ClosestPoint(q.Location)));
            rise = Math.Max(rise, (p.Location - q.Location) * plane.ZAxis);
            extent = Math.Max(extent, plane.ClosestPoint(q.Location).DistanceTo(plane.ClosestPoint(anchor)));
            depthExtent = Math.Max(depthExtent, Math.Abs((q.Location - anchor) * plane.ZAxis));
        }
        double variation = 2 * weightError / minWeight;
        return separation + variation * extent + guard <= radius &&
            (!depth || rise + variation * depthExtent + guard <= tol + gap + other.Gap);
    }
}
