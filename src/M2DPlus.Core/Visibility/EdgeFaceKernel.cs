namespace M2DPlus.Core.Visibility;

/// <summary>Experimental parallel planar kernel. No documents, native calls or live geometry.</summary>
public static class EdgeFaceKernel
{
    public static VisibilityResult Solve(IReadOnlyList<PlanarFace> faces, IReadOnlyList<CandidateEdge> edges,
        VisibilityOptions options, CancellationToken token = default)
    {
        if (!double.IsFinite(options.DepthTolerance) || options.DepthTolerance <= 0 || options.Threads < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var prepared = new Face[faces.Count];
        try
        {
            if (options.DepthTolerance < 1e-150) throw new ArithmeticException("Tolerance exceeds output arithmetic range; whole view unresolved");
            // Exact decisions still need representable drawing coordinates. Reserve
            // an explicit roundoff budget for subtraction, interpolation and union.
            foreach (var p in faces.SelectMany(f => f.Loops).SelectMany(l => l).Concat(edges.SelectMany(e => new[] { e.A, e.B })))
            {
                double size = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
                if (!Finite(p) || size >= 1e150 || Math.BitIncrement(size) - size > options.DepthTolerance / 64)
                    throw new ArithmeticException("Drawing coordinate resolution exceeds tolerance; whole view unresolved");
            }
            for (int i = 0; i < faces.Count; i++) { token.ThrowIfCancellationRequested(); prepared[i] = new(faces[i], options.DepthTolerance); }
            if (edges.Any(e => !Finite(e.A) || !Finite(e.B))) throw new ArithmeticException("Nonfinite edge");
        }
        catch (ArithmeticException e)
        {
            return new(edges.Select((_, i) => new EdgeInterval(i, 0, 1, VisibilityClass.Unresolved)).ToArray(), 0, 0, [e.Message]);
        }
        var tree = options.UseIndex ? new FaceTree(prepared) : null;
        var outputs = new EdgeInterval[edges.Count][];
        long tests = 0, fallback = 0;
        Parallel.For(0, edges.Count, new ParallelOptions { MaxDegreeOfParallelism = options.Threads, CancellationToken = token }, ei =>
        {
            var edge = edges[ei];
            if (edge.A.X == edge.B.X && edge.A.Y == edge.B.Y) { outputs[ei] = []; return; }
            if (!double.IsFinite(Math.Abs(edge.B.X - edge.A.X) + Math.Abs(edge.B.Y - edge.A.Y)))
            {
                outputs[ei] = [new(ei, 0, 1, VisibilityClass.Unresolved)]; return;
            }
            var hidden = new List<(double A, double B)>();
            var ids = new List<int>();
            if (tree is null) ids.AddRange(Enumerable.Range(0, faces.Count)); else tree.Query(edge, ids);
            ids.Sort((a, b) => prepared[b].Z.CompareTo(prepared[a].Z));
            long localTests = 0, localExact = 0; bool unresolved = false;
            foreach (int fi in ids)
            {
                if ((localTests++ & 255) == 0) token.ThrowIfCancellationRequested();
                var f = prepared[fi];
                List<(double A, double B)> pieces;
                try
                {
                    if (f.Filtered is null) throw new IndeterminateException();
                    pieces = Clip(f.Filtered, edge, options.DepthTolerance, token);
                }
                catch (IndeterminateException)
                {
                    localExact++;
                    try { pieces = Clip(f.Exact.Value, edge, options.DepthTolerance, token); }
                    catch (EndpointResolutionException) { unresolved = true; break; }
                }
                hidden.AddRange(pieces);
                Merge(hidden);
                if (hidden.Count == 1 && hidden[0].A == 0 && hidden[0].B == 1) break;
            }
            Interlocked.Add(ref tests, localTests); Interlocked.Add(ref fallback, localExact);
            if (unresolved) { outputs[ei] = [new(ei, 0, 1, VisibilityClass.Unresolved)]; return; }
            var result = new List<EdgeInterval>(); double cursor = 0;
            foreach (var h in hidden)
            {
                if (h.A > cursor) result.Add(new(ei, cursor, h.A, VisibilityClass.Visible));
                result.Add(new(ei, h.A, h.B, VisibilityClass.Hidden)); cursor = h.B;
            }
            if (cursor < 1) result.Add(new(ei, cursor, 1, VisibilityClass.Visible));
            outputs[ei] = result.ToArray();
        });
        return new(outputs.SelectMany(x => x).ToArray(), tests, fallback, []);
    }

    private static List<(double A, double B)> Clip<T>(Polygon<T> face, CandidateEdge edge, double tolerance, CancellationToken token) where T : IScalar<T>
    {
        var zero = T.From(0); var one = T.From(1);
        var output = new List<(double, double)>();
        if (T.Compare(face.Normal.Z, zero) == 0) return output;
        var a = P<T>.From(edge.A); var b = P<T>.From(edge.B); var delta = b - a;
        T Gap(P<T> p) => (face.D - P<T>.Dot(face.Normal, p)) / face.Normal.Z - T.From(tolerance);
        var g0 = Gap(a); var g1 = Gap(b);
        int s0 = T.Compare(g0, zero), s1 = T.Compare(g1, zero);
        if (s0 <= 0 && s1 <= 0) return output;
        var lo = zero; var hi = one;
        if (s0 <= 0) lo = g0 / (g0 - g1);
        else if (s1 <= 0) hi = g0 / (g0 - g1);
        var events = new List<T> { lo, hi };
        void Add(T t) { if (T.Compare(t, lo) > 0 && T.Compare(t, hi) < 0) events.Add(t); }
        foreach (var loop in face.Loops)
            for (int i = 0; i < loop.Length; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var p = loop[i]; var q = loop[(i + 1) % loop.Length]; var side = q - p; var w = p - a;
                var den = delta.X * side.Y - delta.Y * side.X;
                if (T.Compare(den, zero) == 0)
                {
                    if (T.Compare(w.X * delta.Y - w.Y * delta.X, zero) != 0) continue;
                    bool useX = T.Compare(delta.X, zero) != 0;
                    Add(useX ? w.X / delta.X : w.Y / delta.Y);
                    var v = q - a; Add(useX ? v.X / delta.X : v.Y / delta.Y);
                }
                else
                {
                    var u = (w.X * delta.Y - w.Y * delta.X) / den;
                    if (T.Compare(u, zero) < 0 || T.Compare(u, one) > 0) continue;
                    Add((w.X * side.Y - w.Y * side.X) / den);
                }
            }
        // List.Sort wraps comparer exceptions; insertion sort lets an uncertain predicate
        // transfer the complete pair to rational arithmetic without losing its cause.
        for (int i = 1; i < events.Count; i++)
        {
            var value = events[i]; int j = i;
            while (j > 0 && T.Compare(events[j - 1], value) > 0) { events[j] = events[j - 1]; j--; }
            events[j] = value;
        }
        for (int i = 0; i + 1 < events.Count; i++)
        {
            if (T.Compare(events[i], events[i + 1]) == 0) continue;
            var mid = (events[i] + events[i + 1]) / T.From(2);
            var x = a.X + delta.X * mid; var y = a.Y + delta.Y * mid;
            bool inside = false, boundary = false;
            foreach (var loop in face.Loops)
                for (int j = 0; j < loop.Length; j++)
                {
                    var p = loop[j]; var q = loop[(j + 1) % loop.Length];
                    var cross = (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
                    int sign = T.Compare(cross, zero);
                    if (sign == 0 && Between(x, p.X, q.X) && Between(y, p.Y, q.Y)) boundary = true;
                    if ((T.Compare(p.Y, y) > 0) == (T.Compare(q.Y, y) > 0)) continue;
                    if (sign == T.Compare(q.Y, p.Y)) inside = !inside;
                }
            double EndPoint(T t)
            {
                double span = Math.BitIncrement(Math.BitIncrement(Math.Abs(edge.B.X - edge.A.X)) + Math.BitIncrement(Math.Abs(edge.B.Y - edge.A.Y)));
                if (t is Filter f && (f.Hi - f.Lo) * span > tolerance * .25)
                    throw new IndeterminateException();
                double value = Math.Clamp(t.Value, 0, 1);
                if (t is Rational exact)
                {
                    var error = (exact - Rational.From(value)) * Rational.From(span);
                    if (error.N.Sign < 0) error = Rational.From(0) - error;
                    if (Rational.Compare(error, Rational.From(tolerance * .25)) > 0) throw new EndpointResolutionException();
                }
                return value;
            }
            if (inside || boundary) output.Add((EndPoint(events[i]), EndPoint(events[i + 1])));
        }
        return output;
    }
    private static bool Between<T>(T x, T a, T b) where T : IScalar<T> =>
        T.Compare(a, b) <= 0 ? T.Compare(x, a) >= 0 && T.Compare(x, b) <= 0 : T.Compare(x, b) >= 0 && T.Compare(x, a) <= 0;
    internal static void Merge(List<(double A, double B)> intervals)
    {
        intervals.Sort((a, b) => a.A.CompareTo(b.A)); int w = 0;
        foreach (var h in intervals.ToArray())
        {
            if (h.B <= h.A) continue;
            if (w > 0 && h.A <= intervals[w - 1].B) intervals[w - 1] = (intervals[w - 1].A, Math.Max(intervals[w - 1].B, h.B));
            else intervals[w++] = h;
        }
        intervals.RemoveRange(w, intervals.Count - w);
    }
    private static bool Finite(Vertex p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private sealed class Face
    {
        public FilterBox Box { get; }
        public double Z { get; }
        public Polygon<Filter>? Filtered { get; }
        public Lazy<Polygon<Rational>> Exact { get; }
        public Face(PlanarFace face, double tolerance)
        {
            if (face.Loops.Length == 0 || face.Loops.Any(l => l.Length < 3 || l.Any(p => !Finite(p))))
                throw new ArithmeticException("Invalid polygon; whole view unresolved");
            Exact = new(() => new Polygon<Rational>(face));
            var all = face.Loops.SelectMany(l => l).ToArray(); Box = FilterBox.From(all); Z = all.Max(p => p.Z);
            try { Filtered = new Polygon<Filter>(face); } catch (IndeterminateException) { }
            // Validate plane residual exactly; a failed input cannot silently cease to occlude.
            var exact = Exact.Value; var n2 = P<Rational>.Dot(exact.Normal, exact.Normal);
            var limit = Rational.From(tolerance) * Rational.From(tolerance) * n2;
            foreach (var p in all)
            {
                var residual = P<Rational>.Dot(exact.Normal, P<Rational>.From(p)) - exact.D;
                if (Rational.Compare(residual * residual, limit) > 0) throw new ArithmeticException("Nonplanar face; whole view unresolved");
            }
        }
    }

    private readonly record struct FilterBox(double X0, double Y0, double X1, double Y1)
    {
        public static FilterBox From(IEnumerable<Vertex> p) => new(p.Min(v => v.X), p.Min(v => v.Y), p.Max(v => v.X), p.Max(v => v.Y));
        public bool Overlaps(FilterBox b) => X0 <= b.X1 && X1 >= b.X0 && Y0 <= b.Y1 && Y1 >= b.Y0;
    }
    private sealed class FaceTree
    {
        private sealed record Node(FilterBox Box, int[]? Faces, Node? Left, Node? Right);
        private readonly Node? _root;
        public FaceTree(Face[] faces)
        {
            Node Build(int[] ids)
            {
                var box = new FilterBox(ids.Min(i => faces[i].Box.X0), ids.Min(i => faces[i].Box.Y0), ids.Max(i => faces[i].Box.X1), ids.Max(i => faces[i].Box.Y1));
                if (ids.Length <= 8) return new(box, ids, null, null);
                bool x = box.X1 - box.X0 >= box.Y1 - box.Y0;
                Array.Sort(ids, (a, b) => (x ? faces[a].Box.X0 + faces[a].Box.X1 : faces[a].Box.Y0 + faces[a].Box.Y1)
                    .CompareTo(x ? faces[b].Box.X0 + faces[b].Box.X1 : faces[b].Box.Y0 + faces[b].Box.Y1));
                return new(box, null, Build(ids[..(ids.Length / 2)]), Build(ids[(ids.Length / 2)..]));
            }
            if (faces.Length > 0) _root = Build(Enumerable.Range(0, faces.Length).ToArray());
        }
        public void Query(CandidateEdge edge, List<int> result)
        {
            var box = FilterBox.From([edge.A, edge.B]);
            void Walk(Node? node)
            {
                if (node is null || !node.Box.Overlaps(box)) return;
                if (node.Faces is not null) result.AddRange(node.Faces);
                else { Walk(node.Left); Walk(node.Right); }
            }
            Walk(_root);
        }
    }
}
