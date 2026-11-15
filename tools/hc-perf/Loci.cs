namespace M2DPlus.HcPerf;

/// <summary>Drawing output reduced to 2D loci in the view frame (u, v), per class, with source provenance.</summary>
internal sealed class Locus
{
    internal const byte Visible = 0, Hidden = 1, Unresolved = 2, Other = 3;
    internal sealed record Piece(byte Class, int Source, double[] Points);
    internal List<Piece> Pieces { get; } = new();
    internal List<string> Sources { get; } = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    internal int Source(string id) { if (!_index.TryGetValue(id, out var i)) { i = Sources.Count; Sources.Add(id); _index[id] = i; } return i; }
    internal double Length(byte cls) => Pieces.Where(p => p.Class == cls).Sum(p => PolyLength(p.Points));
    internal static double PolyLength(double[] p) { var s = 0.0; for (var k = 2; k + 1 < p.Length; k += 2) s += Math.Sqrt(Sq(p[k] - p[k - 2]) + Sq(p[k + 1] - p[k - 1])); return s; }
    private static double Sq(double x) => x * x;

    internal void Save(string file)
    {
        using var w = new BinaryWriter(File.Create(file));
        w.Write(Sources.Count); foreach (var s in Sources) w.Write(s);
        w.Write(Pieces.Count);
        foreach (var p in Pieces) { w.Write(p.Class); w.Write(p.Source); w.Write(p.Points.Length); foreach (var d in p.Points) w.Write(d); }
    }
    internal static Locus Load(string file)
    {
        using var r = new BinaryReader(File.OpenRead(file)); var locus = new Locus();
        var n = r.ReadInt32(); for (var i = 0; i < n; i++) locus.Source(r.ReadString());
        var m = r.ReadInt32();
        for (var i = 0; i < m; i++)
        {
            var cls = r.ReadByte(); var src = r.ReadInt32(); var len = r.ReadInt32(); var pts = new double[len];
            for (var k = 0; k < len; k++) pts[k] = r.ReadDouble();
            locus.Pieces.Add(new(cls, src, pts));
        }
        return locus;
    }
}

internal readonly record struct Seg(double Ax, double Ay, double Bx, double By, int Source)
{
    internal double Length => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
}

/// <summary>Uniform-grid index over segments, for capsule (distance &lt;= tau) coverage queries.</summary>
internal sealed class SegIndex
{
    private readonly List<Seg> _segs;
    private readonly Dictionary<long, List<int>> _cells = new();
    private readonly double _h;
    private readonly int[] _stamp; private int _tick;
    internal IReadOnlyList<Seg> Segs => _segs;
    internal SegIndex(List<Seg> segs, double tau, double extent)
    {
        _segs = segs; _h = Math.Max(tau * 16, extent / 1024); _stamp = new int[segs.Count];
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            foreach (var key in Cells(Math.Min(s.Ax, s.Bx) - tau, Math.Min(s.Ay, s.By) - tau, Math.Max(s.Ax, s.Bx) + tau, Math.Max(s.Ay, s.By) + tau))
            { if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new(); list.Add(i); }
        }
    }
    private IEnumerable<long> Cells(double x0, double y0, double x1, double y1)
    {
        long i0 = (long)Math.Floor(x0 / _h), i1 = (long)Math.Floor(x1 / _h), j0 = (long)Math.Floor(y0 / _h), j1 = (long)Math.Floor(y1 / _h);
        if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4_000_000) throw new InvalidOperationException("Segment index cell overflow");
        for (var i = i0; i <= i1; i++) for (var j = j0; j <= j1; j++) yield return (i << 32) ^ (j & 0xffffffffL);
    }
    /// <summary>Candidate segments near the box; each returned once per call.</summary>
    internal List<int> Near(double x0, double y0, double x1, double y1)
    {
        var result = new List<int>(); _tick++;
        if (_tick == int.MaxValue) { Array.Clear(_stamp); _tick = 1; }
        // Walk only cells along long thin boxes is handled by the caller splitting long segments.
        foreach (var key in Cells(x0, y0, x1, y1))
            if (_cells.TryGetValue(key, out var list))
                foreach (var i in list) if (_stamp[i] != _tick) { _stamp[i] = _tick; result.Add(i); }
        return result;
    }
}

internal sealed record Span(string Kind, byte Class, double Length, double U, double V, string Source, string Note);

internal static class LocusCompare
{
    /// <summary>Parameter interval of segment a (t in [0,1]) within distance tau of segment c-d: a convex capsule, so one interval.</summary>
    internal static bool Capsule(Seg a, Seg b, double tau, out double t0, out double t1)
    {
        double wx = a.Bx - a.Ax, wy = a.By - a.Ay, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        void Disk(double px, double py, ref double lo, ref double hi)
        {
            double fx = a.Ax - px, fy = a.Ay - py, A = wx * wx + wy * wy, B = 2 * (fx * wx + fy * wy), C = fx * fx + fy * fy - tau * tau;
            if (A < 1e-300) { if (C <= 0) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 1); } return; }
            var disc = B * B - 4 * A * C; if (disc < 0) return;
            var s = Math.Sqrt(disc); lo = Math.Min(lo, (-B - s) / (2 * A)); hi = Math.Max(hi, (-B + s) / (2 * A));
        }
        Disk(b.Ax, b.Ay, ref lo, ref hi); Disk(b.Bx, b.By, ref lo, ref hi);
        double ex = b.Bx - b.Ax, ey = b.By - b.Ay, len = Math.Sqrt(ex * ex + ey * ey);
        if (len > 0)
        {
            double ux = ex / len, uy = ey / len, nx = -uy, ny = ux, ax = a.Ax - b.Ax, ay = a.Ay - b.Ay;
            double r0 = double.NegativeInfinity, r1 = double.PositiveInfinity;
            bool Slab(double alpha, double beta, double l, double h, ref double r0, ref double r1)
            {
                if (Math.Abs(beta) < 1e-300) return alpha >= l && alpha <= h;
                var q0 = (l - alpha) / beta; var q1 = (h - alpha) / beta; if (q0 > q1) (q0, q1) = (q1, q0);
                r0 = Math.Max(r0, q0); r1 = Math.Min(r1, q1); return r0 <= r1;
            }
            if (Slab(ax * ux + ay * uy, wx * ux + wy * uy, 0, len, ref r0, ref r1) && Slab(ax * nx + ay * ny, wx * nx + wy * ny, -tau, tau, ref r0, ref r1))
            { lo = Math.Min(lo, r0); hi = Math.Max(hi, r1); }
        }
        t0 = Math.Max(lo, 0); t1 = Math.Min(hi, 1); return t0 < t1;
    }

    /// <summary>Uncovered sub-intervals [t0, t1] of a with respect to the index.</summary>
    internal static List<(double T0, double T1)> Uncovered(in Seg a, SegIndex index, double tau, Func<int, bool>? accept = null)
    {
        var near = index.Near(Math.Min(a.Ax, a.Bx) - tau, Math.Min(a.Ay, a.By) - tau, Math.Max(a.Ax, a.Bx) + tau, Math.Max(a.Ay, a.By) + tau);
        var covered = new List<(double, double)>();
        foreach (var i in near)
        {
            if (accept is not null && !accept(i)) continue;
            if (Capsule(a, index.Segs[i], tau, out var t0, out var t1)) covered.Add((t0, t1));
        }
        covered.Sort();
        var gaps = new List<(double, double)>(); var at = 0.0;
        foreach (var (t0, t1) in covered) { if (t0 > at) gaps.Add((at, t0)); at = Math.Max(at, t1); if (at >= 1) break; }
        if (at < 1) gaps.Add((at, 1));
        return gaps;
    }

    internal static List<Seg> Segments(Locus locus, byte cls, double maxLength)
    {
        var result = new List<Seg>();
        foreach (var p in locus.Pieces)
        {
            if (p.Class != cls || p.Points.Length < 4) continue;
            for (var k = 2; k + 1 < p.Points.Length; k += 2)
            {
                double ax = p.Points[k - 2], ay = p.Points[k - 1], bx = p.Points[k], by = p.Points[k + 1];
                var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len <= 0) continue;
                // Split long segments so that index queries stay local.
                var n = Math.Max(1, (int)Math.Ceiling(len / maxLength));
                for (var s = 0; s < n; s++)
                    result.Add(new(ax + (bx - ax) * s / n, ay + (by - ay) * s / n, ax + (bx - ax) * (s + 1) / n, ay + (by - ay) * (s + 1) / n, p.Source));
            }
        }
        return result;
    }

    /// <summary>Removes parts of hidden segments that coincide (within tau) with visible loci of the same drawing.</summary>
    internal static List<Seg> Minus(List<Seg> segs, SegIndex visible, double tau)
    {
        var result = new List<Seg>();
        foreach (var s in segs)
            foreach (var (t0, t1) in Uncovered(s, visible, tau))
                if ((t1 - t0) * s.Length > 0) result.Add(new(s.Ax + (s.Bx - s.Ax) * t0, s.Ay + (s.By - s.Ay) * t0, s.Ax + (s.Bx - s.Ax) * t1, s.Ay + (s.By - s.Ay) * t1, s.Source));
        return result;
    }

    internal sealed class Result
    {
        public double Tau { get; init; }
        public Dictionary<string, double> Lengths { get; } = new();
        public Dictionary<string, double> Unmatched { get; } = new();
        public List<Span> Spans { get; } = new();
        public List<Span> Significant { get; } = new();
        public int SignificantSpans { get; set; }
        public double SignificantLength { get; set; }
        public int PointMismatches { get; set; }
        /// <summary>Disagreement only in whether native marked a piece Unresolved (native Unset is order/scene dependent).</summary>
        public double UnresolvedSwapLength { get; set; }
        /// <summary>Significant disagreement on sources still solved by native (native's composition/order instability).</summary>
        public int NativeSolvedSpans { get; set; }
        public double NativeSolvedLength { get; set; }
        public double ExtraCulledHiddenLength { get; set; }
    }

    /// <summary>Locus comparison per class, after removing hidden loci that coincide with visible loci in the same drawing.</summary>
    internal static Result Compare(Locus ours, Locus reference, double tau, double extent, Func<string, bool>? direct = null, Func<string, bool>? culled = null, bool bothSidesOurs = false)
    {
        var result = new Result { Tau = tau };
        var maxLength = Math.Max(tau * 64, extent / 256);
        var sets = new Dictionary<(int, byte), List<Seg>>();
        var indexes = new Dictionary<(int, byte), SegIndex>();
        var loci = new[] { ours, reference };
        for (var side = 0; side < 2; side++)
        {
            var visible = Segments(loci[side], Locus.Visible, maxLength);
            var vIndex = new SegIndex(visible, tau, extent);
            sets[(side, Locus.Visible)] = visible; indexes[(side, Locus.Visible)] = vIndex;
            // Hidden loci coincident with a visible or unresolved locus of the same drawing are reported once (as that locus).
            var shown = new SegIndex(visible.Concat(Segments(loci[side], Locus.Unresolved, maxLength)).ToList(), tau, extent);
            var hidden = Minus(Segments(loci[side], Locus.Hidden, maxLength), shown, tau);
            sets[(side, Locus.Hidden)] = hidden; indexes[(side, Locus.Hidden)] = new SegIndex(hidden, tau, extent);
            var unresolved = Segments(loci[side], Locus.Unresolved, maxLength);
            sets[(side, Locus.Unresolved)] = unresolved; indexes[(side, Locus.Unresolved)] = new SegIndex(unresolved, tau, extent);
        }
        string[] names = { "Visible", "Hidden", "Unresolved" };
        for (byte cls = 0; cls < 3; cls++)
        {
            result.Lengths["ours." + names[cls]] = sets[(0, cls)].Sum(s => s.Length);
            result.Lengths["ref." + names[cls]] = sets[(1, cls)].Sum(s => s.Length);
            for (var side = 0; side < 2; side++)
            {
                var kind = side == 0 ? "extra" : "missing"; var other = 1 - side; var total = 0.0;
                var raw = new List<(double Len, double U, double V, int Src)>();
                foreach (var s in sets[(side, cls)])
                    foreach (var (t0, t1) in Uncovered(s, indexes[(other, cls)], tau))
                    {
                        var len = (t1 - t0) * s.Length; if (len <= 0) continue;
                        total += len; var tm = (t0 + t1) / 2;
                        raw.Add((len, s.Ax + (s.Bx - s.Ax) * tm, s.Ay + (s.By - s.Ay) * tm, s.Source));
                    }
                result.Unmatched[$"{kind}.{names[cls]}"] = total;
                // Merge adjacent pieces into spans by source and proximity (pieces come from split segments).
                foreach (var g in raw.GroupBy(r => (r.Src, (long)Math.Floor(r.U / (tau * 200)), (long)Math.Floor(r.V / (tau * 200)))))
                {
                    var len = g.Sum(r => r.Len); var first = g.First();
                    var src = (side == 0 ? ours : reference).Sources[first.Src];
                    // Category: what the other drawing has at this place.
                    var probe = new Seg(first.U - tau * 1e-3, first.V, first.U + tau * 1e-3, first.V, -1);
                    bool Has(byte oc) => indexes[(other, oc)].Near(first.U - tau, first.V - tau, first.U + tau, first.V + tau).Any(i => Capsule(probe, indexes[(other, oc)].Segs[i], tau, out _, out _));
                    var category = cls == Locus.Unresolved ? (Has(Locus.Visible) || Has(Locus.Hidden) ? "unresolved-swap" : kind)
                        : Has(Locus.Unresolved) ? "unresolved-swap" : Has((byte)(1 - cls)) ? "class-swap" : kind;
                    var span = new Span(category, cls, len, first.U, first.V, src, kind);
                    if (len > 2 * tau)
                    {
                        if (category == "unresolved-swap") result.UnresolvedSwapLength += len;
                        else if (direct is not null && !direct(src)) { result.NativeSolvedSpans++; result.NativeSolvedLength += len; }
                        // Reported (not excused): extra hidden loci on a culled source. Each needs its own evidence (see HILLCLIMB.md).
                        else if (cls == Locus.Hidden && (side == 0 || bothSidesOurs) && category == "extra" && culled is not null && culled(src))
                        { result.ExtraCulledHiddenLength += len; result.SignificantSpans++; result.SignificantLength += len; result.Significant.Add(span with { Kind = "extra-culled-hidden" }); }
                        else { result.SignificantSpans++; result.SignificantLength += len; result.Significant.Add(span); }
                    }
                    result.Spans.Add(span);
                }
            }
        }
        // Visible points.
        var op = ours.Pieces.Where(p => p.Points.Length == 2).Select(p => (p.Points[0], p.Points[1])).ToList();
        var rp = reference.Pieces.Where(p => p.Points.Length == 2).Select(p => (p.Points[0], p.Points[1])).ToList();
        result.PointMismatches = op.Count(a => !rp.Any(b => Math.Abs(a.Item1 - b.Item1) <= tau && Math.Abs(a.Item2 - b.Item2) <= tau)) +
            rp.Count(a => !op.Any(b => Math.Abs(a.Item1 - b.Item1) <= tau && Math.Abs(a.Item2 - b.Item2) <= tau));
        result.Spans.Sort((a, b) => b.Length.CompareTo(a.Length));
        if (result.Spans.Count > 40) result.Spans.RemoveRange(40, result.Spans.Count - 40);
        result.Significant.Sort((a, b) => b.Length.CompareTo(a.Length));
        if (result.Significant.Count > 20) result.Significant.RemoveRange(20, result.Significant.Count - 20);
        return result;
    }
}
