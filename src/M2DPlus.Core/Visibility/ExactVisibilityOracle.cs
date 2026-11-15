namespace M2DPlus.Core.Visibility;

/// <summary>Slow reference: all edges × all faces, rational event arrangement, no index or early out.
/// Intentionally independent of the production clipper and its interval union.</summary>
public static partial class ExactVisibilityOracle
{
    public static VisibilityResult Solve(IReadOnlyList<PlanarFace> faces, IReadOnlyList<CandidateEdge> edges,
        double depthTolerance, CancellationToken token = default, int threads = 1)
    {
        var zero = Rational.From(0); var one = Rational.From(1); var two = Rational.From(2);
        var polygons = faces.Select(f => new Polygon<Rational>(f)).ToArray();
        var bounds = polygons.Select(p =>
        {
            var vertices = p.Loops.SelectMany(l => l).ToArray();
            return (X0: vertices.Select(v => v.X).Min(), X1: vertices.Select(v => v.X).Max(),
                Y0: vertices.Select(v => v.Y).Min(), Y1: vertices.Select(v => v.Y).Max());
        }).ToArray();
        var outputs = new EdgeInterval[edges.Count][]; long tests = 0;
        Parallel.For(0, edges.Count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, ei =>
        {
            token.ThrowIfCancellationRequested();
            var result = new List<EdgeInterval>(); long localTests = 0;
            var e = edges[ei]; if (e.A.X == e.B.X && e.A.Y == e.B.Y) { outputs[ei] = []; return; }
            var a = P<Rational>.From(e.A); var b = P<Rational>.From(e.B); var v = b - a;
            var spans = new List<(Rational A, Rational B)>();
            for (int fi = 0; fi < polygons.Length; fi++)
            {
                if ((localTests++ & 255) == 0) token.ThrowIfCancellationRequested();
                var p = polygons[fi]; var box = bounds[fi];
                // Exact scalar broad phase, still O(edges × faces): no spatial index,
                // ordering or early termination after finding complete coverage.
                if ((a.X.CompareTo(box.X0) < 0 && b.X.CompareTo(box.X0) < 0)
                    || (a.X.CompareTo(box.X1) > 0 && b.X.CompareTo(box.X1) > 0)
                    || (a.Y.CompareTo(box.Y0) < 0 && b.Y.CompareTo(box.Y0) < 0)
                    || (a.Y.CompareTo(box.Y1) > 0 && b.Y.CompareTo(box.Y1) > 0)) continue;
                if (Rational.Compare(p.Normal.Z, zero) == 0) continue;
                var events = new SortedSet<Rational> { zero, one };
                void Event(Rational t) { if (t.CompareTo(zero) > 0 && t.CompareTo(one) < 0) events.Add(t); }
                var h0 = (p.D - P<Rational>.Dot(p.Normal, a)) / p.Normal.Z - Rational.From(depthTolerance);
                var h1 = (p.D - P<Rational>.Dot(p.Normal, b)) / p.Normal.Z - Rational.From(depthTolerance);
                // This exact per-pair rejection is the depth half-space itself, not
                // an edge early-out: every face is still visited and counted.
                if (h0.CompareTo(zero) <= 0 && h1.CompareTo(zero) <= 0) continue;
                if (h0.CompareTo(h1) != 0) Event(h0 / (h0 - h1));
                foreach (var loop in p.Loops)
                    for (int j = 0; j < loop.Length; j++)
                    {
                        var c = loop[j]; var d = loop[(j + 1) % loop.Length];
                        var w = d - c; var u = c - a;
                        var determinant = v.X * w.Y - v.Y * w.X;
                        if (determinant.CompareTo(zero) == 0)
                        {
                            if ((u.X * v.Y - u.Y * v.X).CompareTo(zero) != 0) continue;
                            if (v.X.CompareTo(zero) != 0) { Event((c.X - a.X) / v.X); Event((d.X - a.X) / v.X); }
                            else { Event((c.Y - a.Y) / v.Y); Event((d.Y - a.Y) / v.Y); }
                        }
                        else
                        {
                            // Including crossings of the infinite support is safe: extra events only subdivide.
                            Event((u.X * w.Y - u.Y * w.X) / determinant);
                        }
                    }
                var ts = events.ToArray();
                for (int j = 1; j < ts.Length; j++)
                {
                    var t = (ts[j - 1] + ts[j]) / two;
                    if ((h0 + (h1 - h0) * t).CompareTo(zero) <= 0) continue;
                    var x = a.X + v.X * t; var y = a.Y + v.Y * t;
                    int winding = 0; bool boundary = false;
                    foreach (var loop in p.Loops)
                    {
                        int loopWinding = 0;
                        for (int k = 0; k < loop.Length; k++)
                        {
                            var c = loop[k]; var d = loop[(k + 1) % loop.Length];
                            var orient = ((d.X - c.X) * (y - c.Y) - (d.Y - c.Y) * (x - c.X)).CompareTo(zero);
                            bool bx = (x - c.X).CompareTo(zero) * (x - d.X).CompareTo(zero) <= 0;
                            bool by = (y - c.Y).CompareTo(zero) * (y - d.Y).CompareTo(zero) <= 0;
                            boundary |= orient == 0 && bx && by;
                            if (c.Y.CompareTo(y) <= 0 && d.Y.CompareTo(y) > 0 && orient > 0) loopWinding++;
                            if (c.Y.CompareTo(y) > 0 && d.Y.CompareTo(y) <= 0 && orient < 0) loopWinding--;
                        }
                        winding += Math.Abs(loopWinding) & 1;
                    }
                    if (boundary || (winding & 1) != 0) spans.Add((ts[j - 1], ts[j]));
                }
            }
            var allEvents = new SortedSet<Rational> { zero, one };
            foreach (var span in spans) { allEvents.Add(span.A); allEvents.Add(span.B); }
            var ends = allEvents.ToArray();
            for (int j = 1; j < ends.Length; j++)
            {
                var t = (ends[j - 1] + ends[j]) / two;
                bool hidden = spans.Any(s => s.A.CompareTo(t) <= 0 && s.B.CompareTo(t) >= 0);
                var classification = hidden ? VisibilityClass.Hidden : VisibilityClass.Visible;
                if (result.Count > 0 && result[^1].Edge == ei && result[^1].Class == classification)
                    result[^1] = result[^1] with { End = ends[j].Value };
                else result.Add(new(ei, ends[j - 1].Value, ends[j].Value, classification));
            }
            outputs[ei] = result.ToArray(); Interlocked.Add(ref tests, localTests);
        });
        return new(outputs.SelectMany(x => x).ToArray(), tests, tests, []);
    }
}
