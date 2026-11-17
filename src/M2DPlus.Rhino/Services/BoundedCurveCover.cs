using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Certificates for an entire stretch, never a vote at a few points. Positive rational NURBS lie in
/// their control hull: its radius about the sample bounds projected travel (and hence the 1-Lipschitz distance
/// to any fixed witness). Its maximum depth bounds the depth condition without assuming a continuous nearest
/// parameter on the covering curve; that parameter can jump at a fold or between two covering pieces.</summary>
internal static class BoundedCurveCover
{
    internal sealed record Piece(Curve World, Curve Flat, double Gap);
    internal sealed class WorkBudget(int limit = 200000)
    {
        internal int Used { get; private set; }
        internal int CapHits { get; set; }
        internal int FirstOrderProofs { get; set; }
        internal bool Spend() { if (Used >= limit) return false; Used++; return true; }
        internal int Limit => limit;
    }

    internal static bool Covers(Curve world, Curve flat, Interval domain, IReadOnlyList<Piece> others,
        Plane plane, double tolerance, double gap, bool depth, CancellationToken token = default, int cap = 32768, WorkBudget? budget = null)
    {
        budget ??= new WorkBudget();
        double Depth(Point3d p) => (p - plane.Origin) * plane.ZAxis;
        var stack = new Stack<Interval>(); stack.Push(domain); int samples = 0;
        while (stack.TryPop(out var interval))
        {
            token.ThrowIfCancellationRequested();
            if (++samples > cap || !budget.Spend()) { budget.CapHits++; return false; }
            using var part = world.Trim(interval); using var n = part?.ToNurbsCurve();
            if (n is null || n.Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight))) return false;
            // This also handles exact circles efficiently: identical bases let the difference curve be bounded by
            // the differences of corresponding control points, without millions of tolerance-spaced samples.
            bool certified = false;
            foreach (var v in others)
            {
                if (!budget.Spend()) { budget.CapHits++; return false; }
                if (Paired(n, v, plane, tolerance, gap, depth, out bool firstOrder))
                { if (firstOrder) budget.FirstOrderProofs++; certified = true; break; }
            }
            if (certified) continue;
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
                if (!budget.Spend()) { budget.CapHits++; return false; }
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
            bool centre = false;
            foreach (var v in others)
            {
                if (!budget.Spend()) { budget.CapHits++; return false; }
                if (v.Flat.ClosestPoint(at, out double t, tolerance / 2 + gap + v.Gap) &&
                    (!depth || Depth(world.PointAt(middle)) <= Depth(v.World.PointAt(t)) + tolerance + gap + v.Gap)) { centre = true; break; }
            }
            if (!centre) return false;
            stack.Push(new(middle, interval.Max)); stack.Push(new(interval.Min, middle));
        }
        return true;
    }

    private static bool Paired(NurbsCurve mine, Piece other, Plane plane, double tol, double gap, bool depth, out bool firstOrder)
    {
        firstOrder = false;
        var a = plane.ClosestPoint(mine.PointAtStart); var b = plane.ClosestPoint(mine.PointAtEnd);
        double radius = tol / 2 + gap + other.Gap;
        if (!other.Flat.ClosestPoint(a, out double ta, radius)) return false;
        using var aligned = other.World.IsClosed ? other.World.DuplicateCurve() : null;
        Curve world = other.World, flat = other.Flat;
        if (aligned is not null)
        {
            if (!aligned.ChangeClosedCurveSeam(ta)) return false;
            var tangent = aligned.TangentAtStart; tangent -= plane.Normal * (tangent * plane.Normal);
            if (tangent * mine.TangentAtStart < 0) aligned.Reverse();
            // Duplicate after aligning: parameters must match the world curve used as the witness.
            world = aligned;
            ta = aligned.Domain.Min;
        }
        using var alignedFlat = aligned?.DuplicateCurve();
        if (alignedFlat is not null)
        {
            if (!alignedFlat.Transform(Transform.PlanarProjection(plane))) return false;
            flat = alignedFlat;
        }
        double tb;
        if (mine.IsClosed && world.IsClosed) tb = world.Domain.Max;
        else if (!flat.ClosestPoint(b, out tb, radius)) return false;
        if (ta == tb) return false;
        using var part = world.Trim(new Interval(Math.Min(ta, tb), Math.Max(ta, tb)));
        using var n = part?.ToNurbsCurve();
        if (n is null) return false;
        if (tb < ta) n.Reverse();
        if (TangentCertificate(mine, n, plane, tol, gap + other.Gap, depth)) { firstOrder = true; return true; }
        return Parametric(mine, n, plane, tol, gap + other.Gap, depth);
    }

    private static bool Parametric(NurbsCurve mine, NurbsCurve n, Plane plane, double tol, double gap, bool depth)
    {
        double radius = tol / 2 + gap;
        if (mine.Degree != n.Degree || mine.Points.Count != n.Points.Count || mine.Knots.Count != n.Knots.Count) return false;
        // Equal normalised knots give the same polynomial basis. Bound the rational weight difference too:
        // ||alpha-beta||_1 <= 2 max|w-ratio*v| / min(w), where alpha,beta are the normalised rational bases.
        using var m = mine.ToNurbsCurve();
        m.Domain = new(0, 1); n.Domain = new(0, 1);
        for (int k = 0; k < m.Knots.Count; k++) if (m.Knots[k] != n.Knots[k]) return false;
        double ratio = m.Points[0].Weight / n.Points[0].Weight;
        double guard = 1e-12 * (1 + mine.PointAtStart.DistanceTo(Point3d.Origin));
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
            (!depth || rise + variation * depthExtent + guard <= tol + gap);
    }

    /// <summary>First-order cancellation without assuming matching knots or a continuous closest parameter. Along
    /// the common tangent/chord, V contains every coordinate between its endpoints by continuity. Both control hulls
    /// bound sideways departure from that line (quadratic in length on smooth curves). Depth is detrended by the
    /// same affine coordinate, so the certificate does not pay a first-order travel penalty there either.</summary>
    private static bool TangentCertificate(NurbsCurve u, NurbsCurve v, Plane plane, double tol, double gap, bool depth)
    {
        if (v.Points.Any(p => !(p.Weight > 0) || !double.IsFinite(p.Weight))) return false;
        var origin = u.PointAtStart; var direction = u.PointAtEnd - origin;
        direction -= plane.Normal * (direction * plane.Normal); if (!direction.Unitize()) return false;
        var side = Vector3d.CrossProduct(plane.Normal, direction);
        double extent = (u.PointAtEnd - origin) * direction;
        double slope = ((u.PointAtEnd - origin) * plane.Normal) / extent;
        (double X0, double X1, double S0, double S1, double Z0, double Z1) Hull(NurbsCurve c)
        {
            double x0 = double.PositiveInfinity, s0 = x0, z0 = x0, x1 = double.NegativeInfinity, s1 = x1, z1 = x1;
            foreach (var p in c.Points)
            {
                var w = p.Location - origin; double x = w * direction, s = w * side, z = w * plane.Normal - slope * x;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); s0 = Math.Min(s0, s); s1 = Math.Max(s1, s); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            return (x0, x1, s0, s1, z0, z1);
        }
        var a = Hull(u); var b = Hull(v);
        double vx0 = (v.PointAtStart - origin) * direction, vx1 = (v.PointAtEnd - origin) * direction;
        double miss = Math.Max(0, Math.Max(Math.Min(vx0, vx1) - a.X0, a.X1 - Math.Max(vx0, vx1)));
        double across = Math.Max(Math.Abs(a.S0 - b.S1), Math.Abs(a.S1 - b.S0));
        double guard = 1e-12 * (1 + origin.DistanceTo(Point3d.Origin) + Math.Abs(extent) + Math.Abs(slope));
        return Math.Sqrt(across * across + miss * miss) + guard <= tol / 2 + gap &&
            (!depth || a.Z1 - b.Z0 + Math.Abs(slope) * miss + guard <= tol + gap);
    }
}
