using Rhino.Geometry;

// Unroll a cylindrical trim into (arc length, axial height). A query can use
// parity away from the trim. Near the sampled boundary the caller asks Rhino.
internal sealed class CylinderTrims
{
    private readonly record struct Segment(double X, double Y, double U, double V);
    private readonly List<Segment>[] bins = new List<Segment>[64];
    private readonly Point3d origin;
    private readonly Vector3d axis, first, second;
    private readonly double radius, period, guard;
    private CylinderTrims(Cylinder cylinder, double guard)
    {
        origin = cylinder.Center; axis = cylinder.Axis; radius = cylinder.Radius; period = 2 * Math.PI * radius;
        var frame = new Plane(origin, axis);
        first = frame.XAxis; second = frame.YAxis; this.guard = guard;
        for (int i = 0; i < bins.Length; i++) bins[i] = new();
    }
    private (double X, double Y) Chart(Point3d p)
    {
        var v = p - origin;
        return ((Math.Atan2(v * second, v * first) + Math.PI) * radius, v * axis);
    }
    private int Bin(double x) => Math.Clamp((int)(x * bins.Length / period), 0, bins.Length - 1);
    internal static CylinderTrims? TryBuild(BrepFace face, Cylinder cylinder, double tol)
    {
        var edges = new List<BrepEdge>();
        foreach (var loop in face.Loops)
            foreach (var trim in loop.Trims)
            {
                if (trim.TrimType == BrepTrimType.Seam) continue;
                if (trim.Edge is not { } e || !double.IsFinite(e.Tolerance)) return null;
                edges.Add(e);
            }
        if (edges.Count == 0) return null;
        var result = new CylinderTrims(cylinder, Math.Max(100 * tol, 2 * edges.Max(e => e.Tolerance) + 60 * tol));
        foreach (var edge in edges)
        {
            using var poly = edge.ToPolyline(20 * tol, .04, 0, 0);
            if (poly is null) return null;
            var points = poly.ToPolyline();
            var a = result.Chart(points[0]);
            for (int j = 1; j < points.Count; j++)
            {
                var b = result.Chart(points[j]);
                var delta = b.X - a.X;
                if (Math.Abs(delta) > result.period * .5) delta -= Math.Sign(delta) * result.period;
                for (int turn = -1; turn <= 1; turn++)
                {
                    var x = a.X + turn * result.period;
                    var end = x + delta;
                    double lo = Math.Min(x, end) - result.guard, hi = Math.Max(x, end) + result.guard;
                    if (hi < 0 || lo > result.period) continue;
                    var seg = new Segment(x, a.Y, end, b.Y);
                    for (int k = result.Bin(lo); k <= result.Bin(hi); k++) result.bins[k].Add(seg);
                }
                a = b;
            }
        }
        return result;
    }
    internal bool? Contains(Point3d q)
    {
        var (x, y) = Chart(q);
        bool inside = false;
        foreach (var s in bins[Bin(x)])
        {
            double dx = s.U - s.X, dy = s.V - s.Y;
            if (x < Math.Min(s.X, s.U) - guard || x > Math.Max(s.X, s.U) + guard) continue;
            if (y >= Math.Min(s.Y, s.V) - guard && y <= Math.Max(s.Y, s.V) + guard)
            {
                double length2 = dx * dx + dy * dy;
                double f = length2 > 0 ? Math.Clamp(((x - s.X) * dx + (y - s.Y) * dy) / length2, 0, 1) : 0;
                double ex = x - s.X - f * dx, ey = y - s.Y - f * dy;
                if (ex * ex + ey * ey <= guard * guard) return null;
            }
            if ((s.X > x) != (s.U > x) && s.Y + dy * (x - s.X) / dx > y) inside = !inside;
        }
        return inside;
    }
}
