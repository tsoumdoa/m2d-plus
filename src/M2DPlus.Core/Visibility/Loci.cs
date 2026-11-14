namespace M2DPlus.Core.Visibility;

public readonly record struct Locus(double X0, double Y0, double X1, double Y1, VisibilityClass Class, string Source)
{
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
}

/// <summary>Deterministic tolerance-based support-line union; visible wins over hidden at one locus.</summary>
public static class VisibilityLoci
{
    private sealed class Group(double slope, double offset, bool x)
    {
        public double Slope = slope, Offset = offset;
        public bool X = x;
        public List<(double A, double B)>[] Spans = [[], [], []];
        public string Source = "";
    }
    public static Locus[] Create(IReadOnlyList<CandidateEdge> edges, VisibilityResult result, double tolerance)
    {
        var raw = result.Intervals.Select(i =>
        {
            var e = edges[i.Edge];
            Vertex At(double t) => t == 0 ? e.A : t == 1 ? e.B : new(e.A.X + (e.B.X - e.A.X) * t, e.A.Y + (e.B.Y - e.A.Y) * t, 0);
            var a = At(i.Start); var b = At(i.End);
            return new Locus(a.X, a.Y, b.X, b.Y, i.Class, e.Source);
        }).ToArray();
        return Union(raw, tolerance);
    }
    public static Locus[] Union(IReadOnlyList<Locus> loci, double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (loci.Count == 0) return [];
        double cx = loci[0].X0, cy = loci[0].Y0;
        double extent = Math.Max(1, loci.Max(l => Math.Max(Math.Max(Math.Abs(l.X0 - cx), Math.Abs(l.Y0 - cy)),
            Math.Max(Math.Abs(l.X1 - cx), Math.Abs(l.Y1 - cy)))));
        double step = Math.Max(tolerance * .1, 1e-10), angle = step / extent;
        var buckets = new Dictionary<(bool, long, long), List<Group>>(); var groups = new List<Group>();
        // A stable order makes duplicate ownership independent of extraction order.
        foreach (var l in loci.OrderBy(l => l.Source, StringComparer.Ordinal).ThenBy(l => l.X0).ThenBy(l => l.Y0))
        {
            double dx = l.X1 - l.X0, dy = l.Y1 - l.Y0;
            if (dx == 0 && dy == 0) continue;
            bool x = Math.Abs(dx) >= Math.Abs(dy); double slope = x ? dy / dx : dx / dy;
            double offset = x ? (l.Y0 - cy) - slope * (l.X0 - cx) : (l.X0 - cx) - slope * (l.Y0 - cy);
            long qa = (long)Math.Round(slope / angle), qb = (long)Math.Round(offset / step);
            Group? group = null;
            for (long da = -1; da <= 1 && group is null; da++)
                for (long db = -1; db <= 1 && group is null; db++)
                    if (buckets.TryGetValue((x, qa + da, qb + db), out var candidates))
                        group = candidates.FirstOrDefault(g => Math.Abs(g.Slope - slope) <= angle && Math.Abs(g.Offset - offset) <= step);
            if (group is null)
            {
                group = new(slope, offset, x) { Source = l.Source }; groups.Add(group);
                if (!buckets.TryGetValue((x, qa, qb), out var list)) buckets[(x, qa, qb)] = list = [];
                list.Add(group);
            }
            double a = x ? l.X0 - cx : l.Y0 - cy, b = x ? l.X1 - cx : l.Y1 - cy;
            group.Spans[(int)l.Class].Add((Math.Min(a, b), Math.Max(a, b)));
        }
        var output = new List<Locus>();
        foreach (var g in groups)
        {
            foreach (var spans in g.Spans) EdgeFaceKernel.Merge(spans);
            for (int c = 0; c < 3; c++)
                foreach (var span in g.Spans[c])
                {
                    var remaining = new List<(double A, double B)> { span };
                    // Uncertainty takes precedence over confidence, visible over hidden.
                    var subtract = c == 0 ? g.Spans[2] : c == 1 ? g.Spans[2].Concat(g.Spans[0]) : [];
                    foreach (var cut in subtract)
                    {
                        var next = new List<(double A, double B)>();
                        foreach (var p in remaining)
                        {
                            if (cut.B <= p.A || cut.A >= p.B) next.Add(p);
                            else { if (p.A < cut.A) next.Add((p.A, cut.A)); if (p.B > cut.B) next.Add((cut.B, p.B)); }
                        }
                        remaining = next;
                    }
                    foreach (var p in remaining)
                    {
                        double scale = Math.Sqrt(1 + g.Slope * g.Slope);
                        // A hidden residual below drawing tolerance at a visible junction
                        // is the same coincident locus under visible precedence. Preserve
                        // isolated short features; only junction residues are absorbed.
                        if (c == (int)VisibilityClass.Hidden && (p.B - p.A) * scale <= tolerance
                            && g.Spans[0].Any(v => v.A <= p.B + tolerance / scale && v.B >= p.A - tolerance / scale)) continue;
                        output.Add(g.X ? new(p.A + cx, g.Slope * p.A + g.Offset + cy, p.B + cx, g.Slope * p.B + g.Offset + cy, (VisibilityClass)c, g.Source)
                            : new(g.Slope * p.A + g.Offset + cx, p.A + cy, g.Slope * p.B + g.Offset + cx, p.B + cy, (VisibilityClass)c, g.Source));
                    }
                }
        }
        // Collapsed hidden residues at a visible corner are point loci, including
        // corners between different support directions. They inherit Visible.
        var corners = new Dictionary<(long, long), List<(double X, double Y)>>();
        (long, long) Cell(double x, double y) => ((long)Math.Floor((x - cx) / tolerance), (long)Math.Floor((y - cy) / tolerance));
        foreach (var l in output.Where(l => l.Class == VisibilityClass.Visible))
            foreach (var p in new[] { (X: l.X0, Y: l.Y0), (X: l.X1, Y: l.Y1) })
            {
                var key = Cell(p.X, p.Y);
                if (!corners.TryGetValue(key, out var points)) corners[key] = points = [];
                points.Add(p);
            }
        output.RemoveAll(l =>
        {
            if (l.Class != VisibilityClass.Hidden || l.Length > tolerance) return false;
            var (x, y) = Cell(l.X0, l.Y0);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                if (corners.TryGetValue((x + dx, y + dy), out var points))
                    foreach (var p in points)
                    {
                        double ax = l.X0 - p.X, ay = l.Y0 - p.Y, bx = l.X1 - p.X, by = l.Y1 - p.Y;
                        if (ax * ax + ay * ay <= tolerance * tolerance && bx * bx + by * by <= tolerance * tolerance) return true;
                    }
            return false;
        });
        return output.ToArray();
    }

    /// <summary>Symmetric sampled-locus coverage. Reports every failed segment and class separately.</summary>
    public static Coverage Compare(IReadOnlyList<Locus> expected, IReadOnlyList<Locus> actual, double tolerance, double spacing)
    {
        if (!(tolerance > 0) || !(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        var failures = new List<Locus>(); long samples = 0, missed = 0; double missingLength = 0;
        // Uniform grid of segment bounds; long segments go in a separate list to bound memory.
        double cell = Math.Max(spacing * 4, tolerance * 8);
        var grid = new Dictionary<(long, long, VisibilityClass), List<Locus>>(); var large = new List<Locus>();
        long Cell(double value) => (long)Math.Floor(value / cell);
        foreach (var l in actual)
        {
            long x0 = Cell(Math.Min(l.X0, l.X1) - tolerance), x1 = Cell(Math.Max(l.X0, l.X1) + tolerance);
            long y0 = Cell(Math.Min(l.Y0, l.Y1) - tolerance), y1 = Cell(Math.Max(l.Y0, l.Y1) + tolerance);
            if ((double)(x1 - x0 + 1) * (y1 - y0 + 1) > 10000) { large.Add(l); continue; }
            for (long x = x0; x <= x1; x++) for (long y = y0; y <= y1; y++)
            {
                if (!grid.TryGetValue((x, y, l.Class), out var list)) grid[(x, y, l.Class)] = list = [];
                list.Add(l);
            }
        }
        foreach (var e in expected)
        {
            int n = Math.Max(8, (int)Math.Ceiling(e.Length / spacing)); bool fail = false;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, x = e.X0 + (e.X1 - e.X0) * t, y = e.Y0 + (e.Y1 - e.Y0) * t;
                samples++;
                grid.TryGetValue((Cell(x), Cell(y), e.Class), out var candidates);
                bool covered = (candidates?.Any(l => DistanceSquared(l, x, y) <= tolerance * tolerance) ?? false)
                    || large.Any(l => l.Class == e.Class && DistanceSquared(l, x, y) <= tolerance * tolerance);
                if (!covered) { missed++; missingLength += e.Length / (n + 1); fail = true; }
            }
            if (fail) failures.Add(e);
        }
        return new(samples, missed, failures.ToArray(), missingLength);
    }
    private static double DistanceSquared(Locus l, double x, double y)
    {
        double dx = l.X1 - l.X0, dy = l.Y1 - l.Y0, d2 = dx * dx + dy * dy;
        double t = d2 == 0 ? 0 : Math.Clamp(((x - l.X0) * dx + (y - l.Y0) * dy) / d2, 0, 1);
        double ex = x - (l.X0 + t * dx), ey = y - (l.Y0 + t * dy); return ex * ex + ey * ey;
    }
}
public sealed record Coverage(long Samples, long Missed, Locus[] FailedSegments, double SampledMissingLength);
