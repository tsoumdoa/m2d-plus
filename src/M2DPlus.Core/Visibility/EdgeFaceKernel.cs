namespace M2DPlus.Core.Visibility;

/// <summary>Experimental parallel planar kernel. No documents, native calls or live geometry.</summary>
public static class EdgeFaceKernel
{
    /// <summary>Prepared planar occluders for exact point decisions between curve/plane events.</summary>
    public sealed class PointQuery
    {
        private readonly Face[] _faces;
        private readonly FaceTree _tree;
        private readonly double _tolerance;
        public PointQuery(IReadOnlyList<PlanarFace> faces, double tolerance, CancellationToken token = default)
        {
            if (!double.IsFinite(tolerance) || tolerance < 1e-150) throw new ArithmeticException("Unsupported point tolerance");
            _tolerance = tolerance; _faces = new Face[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (faces[i].Loops.SelectMany(l => l).Any(p => !Resolvable(p)))
                    throw new ArithmeticException("Drawing coordinate resolution exceeds tolerance; points unresolved");
                _faces[i] = new(faces[i], tolerance);
            }
            _tree = new(_faces, token);
        }
        private bool Resolvable(Vertex p)
        {
            double size = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
            return Finite(p) && size < 1e150 && Math.BitIncrement(size) - size <= _tolerance / 64;
        }
        public IReadOnlyList<int> OverlappingFaces(double x0, double y0, double x1, double y1)
        {
            var ids = new List<int>(); _tree.Query(new(new(x0,y0,0), new(x1,y1,0), "bounds"), ids); return ids;
        }
        public VisibilityClass Classify(Vertex point, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!Resolvable(point)) return VisibilityClass.Unresolved;
            var ids = new List<int>(); _tree.Query(new(point, point, "point"), ids);
            foreach (int id in ids)
            {
                token.ThrowIfCancellationRequested(); var face = _faces[id];
                bool hidden;
                try
                {
                    if (face.Filtered is null) throw new IndeterminateException();
                    hidden = Hides(face.Filtered, point, _tolerance, token);
                }
                catch (IndeterminateException) { hidden = Hides(face.Exact.Value, point, _tolerance, token); }
                if (hidden) return VisibilityClass.Hidden;
            }
            return VisibilityClass.Visible;
        }
    }

    private static bool Hides<T>(Polygon<T> face, Vertex point, double tolerance, CancellationToken token) where T : IScalar<T>
    {
        var zero = T.From(0); var p = P<T>.From(point);
        if (T.Compare(face.Normal.Z, zero) == 0) return false;
        // Equivalent plane equation, subtracting before multiplying avoids
        // cancellation amplified by survey coordinates. Ties still go to exact.
        var gap = P<T>.Dot(face.Normal, face.Anchor - p) / face.Normal.Z;
        if (T.Compare(gap, T.From(tolerance)) <= 0) return false;
        bool inside = false, boundary = false;
        foreach (var loop in face.Loops)
            for (int j = 0; j < loop.Length; j++)
            {
                if ((j & 255) == 0) token.ThrowIfCancellationRequested();
                var a = loop[j]; var b = loop[(j + 1) % loop.Length];
                var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                int sign = T.Compare(cross, zero);
                if (sign == 0 && Between(p.X, a.X, b.X) && Between(p.Y, a.Y, b.Y)) boundary = true;
                if ((T.Compare(a.Y, p.Y) > 0) == (T.Compare(b.Y, p.Y) > 0)) continue;
                if (sign == T.Compare(b.Y, a.Y)) inside = !inside;
            }
        return inside || boundary;
    }

    public static VisibilityResult Solve(IReadOnlyList<PlanarFace> faces, IReadOnlyList<CandidateEdge> edges,
        VisibilityOptions options, CancellationToken token = default)
    {
        if (!double.IsFinite(options.DepthTolerance) || options.DepthTolerance <= 0 || options.Threads < 1 || options.MaxPairOperations < 1 || options.MaxFacesPerEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var prepared = new Face[faces.Count];
        try
        {
            if (options.DepthTolerance < 1e-150) throw new ArithmeticException("Tolerance exceeds output arithmetic range; whole view unresolved");
            // Exact decisions still need representable drawing coordinates. Reserve
            // an explicit roundoff budget for subtraction, interpolation and union.
            foreach (var p in faces.SelectMany(f => f.Loops).SelectMany(l => l).Concat(edges.SelectMany(e => new[] { e.A, e.B })))
            {
                token.ThrowIfCancellationRequested();
                double size = Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
                if (!Finite(p) || size >= 1e150 || Math.BitIncrement(size) - size > options.DepthTolerance / 64)
                    throw new ArithmeticException("Drawing coordinate resolution exceeds tolerance; whole view unresolved");
            }
            for (int i = 0; i < faces.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (faces[i].Loops.Sum(l => (long)l.Length) > options.MaxPairOperations)
                    throw new ArithmeticException("Planar face preparation work budget exceeded; whole view unresolved");
                prepared[i] = new(faces[i], options.DepthTolerance);
                if ((i & 255) == 0) options.Progress?.Invoke(i, faces.Count);
            }
            if (edges.Any(e => !Finite(e.A) || !Finite(e.B))) throw new ArithmeticException("Nonfinite edge");
        }
        catch (ArithmeticException e)
        {
            return new(edges.Select((_, i) => new EdgeInterval(i, 0, 1, VisibilityClass.Unresolved)).ToArray(), 0, 0, [e.Message]);
        }
        var tree = options.UseIndex ? new FaceTree(prepared, token) : null;
        var outputs = new EdgeInterval[edges.Count][];
        long tests = 0, fallback = 0, exactTicks = 0; int done = 0, budgetEdges = 0;
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
                if (localTests > options.MaxFacesPerEdge) { unresolved = true; Interlocked.Increment(ref budgetEdges); break; }
                var f = prepared[fi];
                IReadOnlyList<(double A, double B)> pieces;
                try
                {
                    if (f.Filtered is null) throw new IndeterminateException();
                    pieces = Clip(f.Filtered, edge, options.DepthTolerance, token, options.MaxPairOperations);
                }
                catch (IndeterminateException)
                {
                    localExact++;
                    long start = options.ObserveWork is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                    try { pieces = Clip(f.Exact.Value, edge, options.DepthTolerance, token, options.MaxPairOperations); }
                    catch (EndpointResolutionException) { unresolved = true; break; }
                    catch (WorkBudgetException) { unresolved = true; Interlocked.Increment(ref budgetEdges); break; }
                    finally { if (options.ObserveWork is not null) Interlocked.Add(ref exactTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
                }
                catch (WorkBudgetException) { unresolved = true; Interlocked.Increment(ref budgetEdges); break; }
                for (int pi = 0; pi < pieces.Count; pi++) InsertHidden(hidden, pieces[pi]);
                if (hidden.Count == 1 && hidden[0].A == 0 && hidden[0].B == 1) break;
            }
            Interlocked.Add(ref tests, localTests); Interlocked.Add(ref fallback, localExact);
            options.Progress?.Invoke(Interlocked.Increment(ref done), edges.Count);
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
        options.ObserveWork?.Invoke(new(fallback, (double)exactTicks / System.Diagnostics.Stopwatch.Frequency, prepared.Count(f => f.Exact.IsValueCreated), budgetEdges));
        return new(outputs.SelectMany(x => x).ToArray(), tests, fallback,
            budgetEdges == 0 ? [] : [$"Planar certificate work budget exceeded for {budgetEdges} candidates; their complete spans are Unresolved."]);
    }

    private sealed class WorkBudgetException : Exception;

    private static IReadOnlyList<(double A, double B)> Clip<T>(Polygon<T> face, CandidateEdge edge, double tolerance, CancellationToken token, int operationLimit) where T : IScalar<T>
    {
        int operations = 0;
        void Step()
        {
            if ((operations++ & 255) == 0) token.ThrowIfCancellationRequested();
            if (operations > operationLimit) throw new WorkBudgetException();
        }
        var zero = T.From(0); var one = T.From(1);
        List<(double, double)>? output = null;
        if (T.Compare(face.Normal.Z, zero) == 0) return Array.Empty<(double, double)>();
        var a = P<T>.From(edge.A); var b = P<T>.From(edge.B); var delta = b - a;
        T Gap(P<T> p) => P<T>.Dot(face.Normal, face.Anchor - p) / face.Normal.Z - T.From(tolerance);
        var g0 = Gap(a); var g1 = Gap(b);
        int s0 = T.Compare(g0, zero), s1 = T.Compare(g1, zero);
        if (s0 <= 0 && s1 <= 0) return Array.Empty<(double, double)>();
        var lo = zero; var hi = one;
        if (s0 <= 0) lo = g0 / (g0 - g1);
        else if (s1 <= 0) hi = g0 / (g0 - g1);
        var events = new List<T> { lo, hi };
        void Add(T t) { if (T.Compare(t, lo) > 0 && T.Compare(t, hi) < 0) events.Add(t); }
        foreach (var loop in face.Loops)
            for (int i = 0; i < loop.Length; i++)
            {
                Step();
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
            while (j > 0 && T.Compare(events[j - 1], value) > 0) { Step(); events[j] = events[j - 1]; j--; }
            events[j] = value;
        }
        for (int i = 0; i + 1 < events.Count; i++)
        {
            Step();
            if (T.Compare(events[i], events[i + 1]) == 0) continue;
            var mid = (events[i] + events[i + 1]) / T.From(2);
            var x = a.X + delta.X * mid; var y = a.Y + delta.Y * mid;
            bool inside = false, boundary = false;
            foreach (var loop in face.Loops)
                for (int j = 0; j < loop.Length; j++)
                {
                    Step();
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
            if (inside || boundary) (output ??= []).Add((EndPoint(events[i]), EndPoint(events[i + 1])));
        }
        return output is null ? Array.Empty<(double, double)>() : output;
    }
    private static bool Between<T>(T x, T a, T b) where T : IScalar<T> =>
        T.Compare(a, b) <= 0 ? T.Compare(x, a) >= 0 && T.Compare(x, b) <= 0 : T.Compare(x, b) >= 0 && T.Compare(x, a) <= 0;
    private static void InsertHidden(List<(double A, double B)> spans, (double A, double B) piece)
    {
        double a = piece.A, b = piece.B;
        if (b <= a) return;
        int first = 0;
        while (first < spans.Count && spans[first].B < a) first++;
        int end = first;
        while (end < spans.Count && spans[end].A <= b)
        {
            a = Math.Min(a, spans[end].A); b = Math.Max(b, spans[end].B); end++;
        }
        if (end == first) spans.Insert(first, (a, b));
        else
        {
            spans[first] = (a, b);
            if (end > first + 1) spans.RemoveRange(first + 1, end - first - 1);
        }
    }
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
            // Three vertices are coplanar by construction, including a degenerate
            // triangle (the existing zero-normal semantics still apply). For larger
            // polygons certify the SAME residual inequality with outward intervals;
            // only an inconclusive certificate needs the exact polygon eagerly.
            if (!(face.Loops.Length == 1 && all.Length == 3) && !CertifiedPlanar(Filtered, all, tolerance))
            {
                var exact = Exact.Value; var n2 = P<Rational>.Dot(exact.Normal, exact.Normal);
                var limit = Rational.From(tolerance) * Rational.From(tolerance) * n2;
                foreach (var p in all)
                {
                    var residual = P<Rational>.Dot(exact.Normal, P<Rational>.From(p) - exact.Anchor);
                    if (Rational.Compare(residual * residual, limit) > 0) throw new ArithmeticException("Nonplanar face; whole view unresolved");
                }
            }
            if (Filtered is null)
            {
                try
                {
                    var exact = Exact.Value;
                    Filtered = new(face, new(Filter.FromExact(exact.Normal.X), Filter.FromExact(exact.Normal.Y), Filter.FromExact(exact.Normal.Z)), Filter.FromExact(exact.D),
                        new(Filter.FromExact(exact.Anchor.X),Filter.FromExact(exact.Anchor.Y),Filter.FromExact(exact.Anchor.Z)));
                }
                catch (IndeterminateException) { }
            }
        }
        private static bool CertifiedPlanar(Polygon<Filter>? polygon, Vertex[] vertices, double tolerance)
        {
            if (polygon is null) return false;
            try
            {
                var limit = Filter.From(tolerance) * Filter.From(tolerance) * P<Filter>.Dot(polygon.Normal, polygon.Normal);
                foreach (var p in vertices)
                {
                    var residual = P<Filter>.Dot(polygon.Normal, P<Filter>.From(p) - polygon.Anchor);
                    if (Filter.Compare(residual * residual, limit) > 0) return false;
                }
                return true;
            }
            catch (IndeterminateException) { return false; }
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
        private readonly Face[] _faces;
        public FaceTree(Face[] faces, CancellationToken token)
        {
            _faces = faces;
            Node Build(int[] ids)
            {
                token.ThrowIfCancellationRequested();
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
                if (node.Faces is not null)
                {
                    foreach (int fi in node.Faces) if (_faces[fi].Box.Overlaps(box)) result.Add(fi);
                }
                else { Walk(node.Left); Walk(node.Right); }
            }
            Walk(_root);
        }
    }
}
