using Rhino.Geometry;

namespace M2DPlus.SectionScore;

/// <summary>Independent graph integration of exact swept-profile surfaces, including C0 knots.</summary>
internal static class ProfileOracle
{
    public static (double Area, int Loops, int Holes)? Try(Brep source, Plane plane, double tolerance)
    {
        var a = Integrate(source, plane, tolerance, 32);
        var b = Integrate(source, plane, tolerance, 64);
        if (a is null || b is null || a.Value.Loops != b.Value.Loops || a.Value.Holes != b.Value.Holes) return null;
        if (Math.Abs(a.Value.Area - b.Value.Area) < 2e-6) return b;
        var c = Integrate(source, plane, tolerance, 128);
        return c is not null && c.Value.Loops == b.Value.Loops && c.Value.Holes == b.Value.Holes &&
            Math.Abs(c.Value.Area - b.Value.Area) < 2e-6 ? c : null;
    }

    private static (double Area, int Loops, int Holes)? Integrate(Brep source, Plane plane, double tol, int samples)
    {
        var traces = new List<Curve>();
        try
        {
            foreach (var face in source.Faces)
            {
                var box = face.GetBoundingBox(Transform.PlaneToPlane(plane, Plane.WorldXY));
                if (box.Min.Z > tol || box.Max.Z < -tol) continue;
                using var surface = face.ToNurbsSurface();
                var points = Trace(surface, face, plane, tol, samples, 0) ?? Trace(surface, face, plane, tol, samples, 1);
                if (points is null) return null;
                traces.Add(new PolylineCurve(points));
            }
            if (traces.Count == 0) return null;
            var joined = Curve.JoinCurves(traces, tol * .01);
            try
            {
                var loops = new List<Point2d[]>();
                foreach (var curve in joined)
                {
                    if (!curve.TryGetPolyline(out var polygon) || polygon[0].DistanceTo(polygon[^1]) > tol * .01) return null;
                    polygon[^1] = polygon[0];
                    loops.Add(polygon.Select(p => { plane.ClosestParameter(p, out var x, out var y); return new Point2d(x, y); }).ToArray());
                }
                var area = 0d; var holes = 0;
                for (var i = 0; i < loops.Count; i++)
                {
                    var depth = Enumerable.Range(0, loops.Count).Count(j => i != j && Contains(loops[j], loops[i][0]));
                    var sum = 0d; for (var k = 1; k < loops[i].Length; k++) sum += loops[i][k - 1].X * loops[i][k].Y - loops[i][k].X * loops[i][k - 1].Y;
                    if (depth % 2 != 0) holes++;
                    area += (depth % 2 == 0 ? 1 : -1) * Math.Abs(sum) / 2;
                }
                return (area, loops.Count, holes);
            }
            finally { foreach (var curve in joined) curve.Dispose(); }
        }
        finally { foreach (var curve in traces) curve.Dispose(); }
    }

    private static List<Point3d>? Trace(NurbsSurface s, BrepFace face, Plane plane, double tol, int count, int profile)
    {
        var domain = s.Domain(profile); var along = s.Domain(1 - profile);
        var knots = (profile == 0 ? s.KnotsU : s.KnotsV).Where(t => t >= domain.Min && t <= domain.Max)
            .Append(domain.Min).Append(domain.Max).Distinct().Order().ToArray();
        var result = new List<Point3d>();
        for (var span = 1; span < knots.Length; span++)
        for (var j = span == 1 ? 0 : 1; j <= count; j++)
        {
            var u = knots[span - 1] + (knots[span] - knots[span - 1]) * j / count;
            Point3d P(double v) => profile == 0 ? s.PointAt(u, v) : s.PointAt(v, u);
            var lo = along.Min; var hi = along.Max; var dl = plane.DistanceTo(P(lo)); var dh = plane.DistanceTo(P(hi));
            if (dl * dh >= 0) return null;
            for (var k = 0; k < 50; k++)
            {
                var mid = (lo + hi) / 2; var d = plane.DistanceTo(P(mid));
                if (Math.Sign(d) == Math.Sign(dl)) { lo = mid; dl = d; } else hi = mid;
            }
            var point = P((lo + hi) / 2);
            if (!face.ClosestPoint(point, out var fu, out var fv) || face.PointAt(fu, fv).DistanceTo(point) > tol * .01 ||
                face.IsPointOnFace(fu, fv) == PointFaceRelation.Exterior) return null;
            result.Add(plane.ClosestPoint(point));
        }
        return result;
    }

    private static bool Contains(Point2d[] loop, Point2d p)
    {
        var inside = false;
        for (var i = 1; i < loop.Length; i++)
        {
            var a = loop[i - 1]; var b = loop[i];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
        }
        return inside;
    }
}
