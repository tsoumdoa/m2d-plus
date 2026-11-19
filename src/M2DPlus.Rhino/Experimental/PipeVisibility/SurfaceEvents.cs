using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.Rhino.Experimental.PipeVisibility;

// Partition each drawing curve at projected boundary crossings and at 3-D body
// crossings, then ask the original solids about a ray within each resulting interval.
internal static class SurfaceEvents
{
    private sealed record Boundary(string Source, Curve Spatial, Curve Flat, BoundingBox Box, bool Draw);

    internal static void Draw(List<PipeGeometry.Input> inputs, Plane frame, Vector3d direction,
        double tol, double angle, int threads,
        List<PipeGeometry.Stroke> output, List<PipeGeometry.Dot> dots,
        CancellationToken token = default, bool tangents = true)
    {
        token.ThrowIfCancellationRequested();
        var eye = -direction;
        eye.Unitize();
        var bodies = inputs.Where(i => i.Geometry is Brep).ToDictionary(i => i.Name, i => (Brep)i.Geometry);
        if (bodies.Values.Any(b => !b.IsSolid || !b.IsValid)) throw new NotSupportedException("Requires valid closed solids");
        var bounds = BoundingBox.Empty;
        foreach (var i in inputs) bounds.Union(i.Geometry.GetBoundingBox(true));
        var reach = 2 * bounds.Diagonal.Length;
        using var pipeRays = new PipeRayFaces(bodies.Values, eye, tol, reach, threads, token);
        var boundaries = new List<Boundary>();
        var projection = Transform.PlanarProjection(frame);
        void Add(List<Boundary> batch, string source, Curve curve, bool draw)
        {
            var flat = curve.DuplicateCurve();
            try
            {
                if (!flat.Transform(projection)) throw new InvalidOperationException("Projection failed");
                var box = flat.GetBoundingBox(true); box.Inflate(tol);
                batch.Add(new(source, curve, flat, box, draw));
            }
            catch { flat.Dispose(); curve.Dispose(); throw; }
        }
        List<PipeGeometry.Stroke>[]? groups = null;
        var transferred = false;
        var batches = new List<Boundary>[inputs.Count];
        try
        {
            token.ThrowIfCancellationRequested();
            var pointOutput = new PipeGeometry.Dot?[inputs.Count];
            void Prepare(int id)
            {
                token.ThrowIfCancellationRequested();
                var input = inputs[id];
                var batch = batches[id] = new();
                if (input.Geometry is Brep b)
                {
                    foreach (var e in b.Edges)
                    {
                        var adjacent = e.AdjacentFaces();
                        bool draw = !e.IsSmoothManifoldEdge(angle) && !(adjacent is [var a, var z] && a == z);
                        Add(batch, input.Name, e.DuplicateCurve(), draw);
                    }
                    foreach (var s in Silhouette.Compute(b, SilhouetteType.Tangent, direction, tol, angle) ?? [])
                        if (s.Curve is { } c) Add(batch, input.Name, c.DuplicateCurve(), tangents);
                }
                else if (input.Geometry is Curve c) Add(batch, input.Name, c.DuplicateCurve(), true);
                else if (input.Geometry is global::Rhino.Geometry.Point dot)
                    pointOutput[id] = new(input.Name, pipeRays.Hidden(dot.Location) ? "Hidden" : "Visible", dot.Location);
                else throw new NotSupportedException(input.Geometry.GetType().Name);
            }
            Parallel.For(0, inputs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, Prepare);
            foreach (var batch in batches) boundaries.AddRange(batch);
            foreach (var point in pointOutput) if (point is not null) dots.Add(point);
            groups = new List<PipeGeometry.Stroke>[boundaries.Count];
            // Both parameter lists are returned by one projected curve/curve intersection.
            var events = new List<(int Index, double T)>[boundaries.Count];
            var shared = new List<double>[boundaries.Count];
            for (int i = 0; i < shared.Length; i++) shared[i] = new();
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token };
            Parallel.For(0, boundaries.Count, options, i =>
            {
                token.ThrowIfCancellationRequested();
                var cuts = events[i] = new();
                var a = boundaries[i];
                for (int j = i; j < boundaries.Count; j++)
                {
                    token.ThrowIfCancellationRequested();
                    var b = boundaries[j];
                    if ((!a.Draw && !b.Draw) || !BoundingBox.Intersection(a.Box, b.Box).IsValid) continue;
                    using var xs = i == j ? Intersection.CurveSelf(a.Flat, tol) : Intersection.CurveCurve(a.Flat, b.Flat, tol, tol);
                    if (xs is null) continue;
                    foreach (var hit in xs)
                    {
                        double height = !hit.IsOverlap
                            ? (b.Spatial.PointAt(hit.ParameterB) - a.Spatial.PointAt(hit.ParameterA)) * eye : 0;
                        if (height >= -64 * tol) cuts.Add((i, hit.ParameterA));
                        if (height <= 64 * tol) cuts.Add((j, hit.ParameterB));
                        if (hit.IsOverlap)
                        {
                            cuts.Add((i, hit.OverlapA.T1)); cuts.Add((j, hit.OverlapB.T0)); cuts.Add((j, hit.OverlapB.T1));
                        }
                    }
                }
            });
            foreach (var items in events) foreach (var (i, t) in items) shared[i].Add(t);
            Parallel.For(0, boundaries.Count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, i =>
            {
                token.ThrowIfCancellationRequested();
                var item = boundaries[i];
                var group = groups[i] = new();
                if (!item.Draw) return;
                var space = item.Spatial; var domain = space.Domain;
                var cuts = new List<double> { domain.Min, domain.Max };
                cuts.AddRange(shared[i]);
                var box = space.GetBoundingBox(true); box.Inflate(tol);
                pipeRays.DepthEvents(space, bodies.GetValueOrDefault(item.Source), cuts);
                cuts.Sort();
                bool? last = null;
                double start = domain.Min, previous = domain.Min;
                void Emit(double end)
                {
                    if (end <= start || last is null) return;
                    var piece = space.Trim(start, end);
                    if (piece is null) return;
                    if (piece.GetLength() <= tol) piece.Dispose();
                    else group.Add(new(item.Source, last.Value ? "Hidden" : "Visible", piece));
                }
                void Change(double t, bool hidden)
                {
                    if (last == hidden) return;
                    Emit(t); start = t; last = hidden;
                }
                double Refine(double a, double b, bool left)
                {
                    for (int j = 0; j < 32 && space.PointAt(a).DistanceTo(space.PointAt(b)) > tol * .5; j++)
                    {
                        var m = (a + b) * .5;
                        if (pipeRays.Hidden(space.PointAt(m)) == left) a = m; else b = m;
                    }
                    return (a + b) * .5;
                }
                foreach (var cut in cuts)
                {
                    token.ThrowIfCancellationRequested();
                    if (cut <= previous || cut > domain.Max) continue;
                    var mid = .5 * (previous + cut);
                    var hidden = pipeRays.Hidden(space.PointAt(mid));
                    // Short source edges near a saddle contact can switch class inside
                    // an interval as the clearance grows beyond document tolerance.
                    // Test both sides and localize a switch on the original CAD curve.
                    if (box.Diagonal.Length < 8192 * tol)
                    {
                        var a = previous + (cut - previous) * .001;
                        var b = cut - (cut - previous) * .001;
                        var left = pipeRays.Hidden(space.PointAt(a)); var right = pipeRays.Hidden(space.PointAt(b));
                        Change(previous, left);
                        if (left != hidden) Change(Refine(a, mid, left), hidden);
                        if (right != hidden) Change(Refine(mid, b, hidden), right);
                    }
                    else Change(previous, hidden);
                    previous = cut;
                }
                Emit(domain.Max);
            });
            token.ThrowIfCancellationRequested();
            foreach (var group in groups) output.AddRange(group);
            transferred = true;
        }
        finally
        {
            foreach (var batch in batches) if (batch is not null)
                foreach (var b in batch) { b.Flat.Dispose(); b.Spatial.Dispose(); }
            if (!transferred && groups is not null) foreach (var group in groups)
                if (group is not null) foreach (var stroke in group) stroke.Curve.Dispose();
        }
    }
}

