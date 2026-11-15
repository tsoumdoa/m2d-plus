namespace M2DPlus.Core.Visibility.Raster;

/// <summary>
/// Drawing-locus convention (shared with the exact planar kernel): where a hidden locus coincides with a visible locus
/// within a tolerance, the visible one wins and the coincident hidden span is removed. Works on 2D segments in the view
/// plane; the parameter interval of a hidden segment within distance <c>tolerance</c> of a visible segment is the
/// intersection of a line with a capsule (convex), so the result is exact up to rounding.
/// </summary>
public sealed class VisibleOverHidden
{
    private readonly List<(double Ax, double Ay, double Bx, double By)> _visible = new();
    private readonly Dictionary<(long, long), List<int>> _cells = new();
    private readonly double _tolerance, _cell;

    public VisibleOverHidden(IEnumerable<(double Ax, double Ay, double Bx, double By)> visible, double tolerance, double cell)
    {
        _tolerance = tolerance; _cell = Math.Max(cell, tolerance * 4);
        foreach (var s in visible)
        {
            var index = _visible.Count; _visible.Add(s);
            foreach (var key in Cells(Math.Min(s.Ax, s.Bx) - tolerance, Math.Min(s.Ay, s.By) - tolerance, Math.Max(s.Ax, s.Bx) + tolerance, Math.Max(s.Ay, s.By) + tolerance))
            { if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new(); list.Add(index); }
        }
    }

    private IEnumerable<(long, long)> Cells(double x0, double y0, double x1, double y1)
    {
        long i0 = (long)Math.Floor(x0 / _cell), i1 = (long)Math.Floor(x1 / _cell), j0 = (long)Math.Floor(y0 / _cell), j1 = (long)Math.Floor(y1 / _cell);
        if ((i1 - i0 + 1) * (j1 - j0 + 1) > 1_000_000) { i1 = i0; j1 = j0; } // degenerate guard: never expected for drawing segments
        for (var i = i0; i <= i1; i++) for (var j = j0; j <= j1; j++) yield return (i, j);
    }

    /// <summary>Parameter intervals of the hidden segment that are not within tolerance of any visible segment.</summary>
    public List<(double T0, double T1)> Remaining(double ax, double ay, double bx, double by)
    {
        var covered = new List<(double, double)>(); var seen = new HashSet<int>();
        foreach (var key in Cells(Math.Min(ax, bx) - _tolerance, Math.Min(ay, by) - _tolerance, Math.Max(ax, bx) + _tolerance, Math.Max(ay, by) + _tolerance))
            if (_cells.TryGetValue(key, out var list))
                foreach (var i in list)
                    if (seen.Add(i) && Capsule(ax, ay, bx, by, _visible[i], _tolerance, out var t0, out var t1)) covered.Add((t0, t1));
        covered.Sort();
        var gaps = new List<(double, double)>(); var at = 0.0;
        foreach (var (t0, t1) in covered) { if (t0 > at) gaps.Add((at, t0)); at = Math.Max(at, t1); if (at >= 1) break; }
        if (at < 1) gaps.Add((at, 1));
        return gaps;
    }

    /// <summary>The t-interval of segment a-b within distance tau of segment s (a convex capsule, so one interval).</summary>
    public static bool Capsule(double ax, double ay, double bx, double by, (double Ax, double Ay, double Bx, double By) s, double tau, out double t0, out double t1)
    {
        double wx = bx - ax, wy = by - ay, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        void Disk(double px, double py)
        {
            double fx = ax - px, fy = ay - py, A = wx * wx + wy * wy, B = 2 * (fx * wx + fy * wy), C = fx * fx + fy * fy - tau * tau;
            if (A < 1e-300) { if (C <= 0) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 1); } return; }
            var disc = B * B - 4 * A * C; if (disc < 0) return;
            var q = Math.Sqrt(disc); lo = Math.Min(lo, (-B - q) / (2 * A)); hi = Math.Max(hi, (-B + q) / (2 * A));
        }
        Disk(s.Ax, s.Ay); Disk(s.Bx, s.By);
        double ex = s.Bx - s.Ax, ey = s.By - s.Ay, len = Math.Sqrt(ex * ex + ey * ey);
        if (len > 0)
        {
            double ux = ex / len, uy = ey / len, nx = -uy, ny = ux, px = ax - s.Ax, py = ay - s.Ay, r0 = double.NegativeInfinity, r1 = double.PositiveInfinity;
            bool Slab(double alpha, double beta, double l, double h)
            {
                if (Math.Abs(beta) < 1e-300) return alpha >= l && alpha <= h;
                var q0 = (l - alpha) / beta; var q1 = (h - alpha) / beta; if (q0 > q1) (q0, q1) = (q1, q0);
                r0 = Math.Max(r0, q0); r1 = Math.Min(r1, q1); return r0 <= r1;
            }
            if (Slab(px * ux + py * uy, wx * ux + wy * uy, 0, len) && Slab(px * nx + py * ny, wx * nx + wy * ny, -tau, tau)) { lo = Math.Min(lo, r0); hi = Math.Max(hi, r1); }
        }
        t0 = Math.Max(lo, 0); t1 = Math.Min(hi, 1); return t0 < t1;
    }
}
