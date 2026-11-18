using Rhino.Geometry;

// Per-view intersection with translated rational quadratic profiles. Transforming
// along a common ruling leaves the plane through the sight ray and ruling fixed.
internal sealed class RuledRay
{
    private sealed record Span(Point4d A, Point4d B, Point4d C, double Min, double Max);
    private readonly List<Span> spans = new();
    private readonly Vector3d eye, transverse, normal;
    private readonly double denom;
    private RuledRay(Vector3d eye, Vector3d ruling)
    {
        this.eye = eye;
        transverse = eye - (eye * ruling) * ruling;
        denom = transverse.SquareLength;
        normal = Vector3d.CrossProduct(eye, ruling);
        normal.Unitize();
    }
    internal static RuledRay? TryBuild(BrepFace face, Vector3d eye, double tol)
    {
        using var s = face.ToNurbsSurface();
        var net = s.Points;
        int d = s.Degree(0) == 1 ? 0 : 1;
        if (s.Degree(d) != 1 || s.Degree(1 - d) != 2) return null;
        int rows = d == 0 ? net.CountU : net.CountV, cols = d == 0 ? net.CountV : net.CountU;
        ControlPoint At(int i, int j) => d == 0 ? net.GetControlPoint(i, j) : net.GetControlPoint(j, i);
        var axis = At(rows - 1, 0).Location - At(0, 0).Location;
        if (!axis.Unitize()) return null;
        for (int i = 1; i < rows; i++)
        {
            var shift = At(i, 0).Location - At(0, 0).Location;
            double ratio = At(i, 0).Weight / At(0, 0).Weight;
            if (Vector3d.CrossProduct(shift, axis).Length > tol * .01) return null;
            for (int j = 1; j < cols; j++)
                if ((At(i, j).Location - At(0, j).Location - shift).Length > tol * .01 ||
                    Math.Abs(At(i, j).Weight / At(0, j).Weight - ratio) > 1e-12) return null;
        }
        var proxy = new RuledRay(eye, axis);
        if (proxy.denom < 1e-12) return null;
        using var iso = s.IsoCurve(1 - d, s.Domain(d).Min);
        using var profile = iso.ToNurbsCurve();
        for (int i = 0; i <= profile.Points.Count - profile.Order; i++)
        {
            if (profile.Knots[i + profile.Degree - 1] >= profile.Knots[i + profile.Degree]) continue;
            using var b = profile.ConvertSpanToBezier(i);
            if (b is null || b.ControlVertexCount != 3) return null;
            var a = b.GetControlVertex4d(0); var m = b.GetControlVertex4d(1); var c = b.GetControlVertex4d(2);
            if (Math.Min(a.W, Math.Min(m.W, c.W)) <= 0) return null;
            double Side(Point4d h) => (h.X * proxy.normal.X + h.Y * proxy.normal.Y + h.Z * proxy.normal.Z) / h.W;
            var x = Side(a); var y = Side(m); var z = Side(c);
            proxy.spans.Add(new(a, m, c, Math.Min(x, Math.Min(y, z)) - tol, Math.Max(x, Math.Max(y, z)) + tol));
        }
        return proxy.spans.Count > 0 ? proxy : null;
    }
    internal bool Hits(Point3d p, BrepFace face, bool flip, BoundingBox box, double tol, Func<double, bool> material)
    {
        double line = (Vector3d)p * normal;
        double Eval(Point4d h) => h.X * normal.X + h.Y * normal.Y + h.Z * normal.Z - line * h.W;
        foreach (var s in spans)
        {
            if (line < s.Min || line > s.Max) continue;
            double a = Eval(s.A), b = Eval(s.B), c = Eval(s.C);
            double A = a - 2 * b + c, B = 2 * (b - a), D = B * B - 4 * A * a;
            if (D < 0) continue;
            bool Check(double t)
            {
                if (!(t >= 0 && t <= 1)) return false;
                double h = 1 - t, k = h * h, m = 2 * h * t, n = t * t;
                double w = k * s.A.W + m * s.B.W + n * s.C.W;
                var q = new Point3d((k * s.A.X + m * s.B.X + n * s.C.X) / w, (k * s.A.Y + m * s.B.Y + n * s.C.Y) / w,
                    (k * s.A.Z + m * s.B.Z + n * s.C.Z) / w);
                double depth = ((q - p) * transverse) / denom;
                if (depth <= 2 * tol) return false;
                var hit = p + depth * eye;
                if (!box.Contains(hit) || !face.ClosestPoint(hit, out double u, out double v) ||
                    face.PointAt(u, v).DistanceTo(hit) > tol || face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) return false;
                var nrm = face.UnderlyingSurface().NormalAt(u, v);
                return (flip ? -1 : 1) * (nrm * eye) > 1e-8 && material(depth);
            }
            if (Math.Abs(A) < 1e-12) { if (Math.Abs(B) > 1e-12 && Check(-a / B)) return true; continue; }
            double root = -.5 * (B + Math.CopySign(Math.Sqrt(D), B));
            if (root != 0 && (Check(root / A) || Check(a / root))) return true;
        }
        return false;
    }
}
