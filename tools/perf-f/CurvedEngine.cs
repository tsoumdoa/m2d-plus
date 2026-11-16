using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.PerfF.CurvedCore;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Environment = System.Environment;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// Curved-engine prototype (hillclimb/curved-engine). Harness only; no product code path is changed.
///   ce:&lt;view&gt; eps:&lt;k&gt; [eps:&lt;k&gt; ...] [t1off] [adjudicate:&lt;n&gt;]
/// Solves the view through the product for the native reference (audit off = raw native classes), then runs the
/// prototype on exactly the inputs the product sent to native HLD:
///   candidates  Brep edges (creases and naked edges) and Rhino's exact tangent silhouettes, cut into short chords;
///   tier 1      exact analytic solids (sphere, cylinder, hollow cylinder), recognised strictly;
///   tier 2      everything else as a welded mesh with a measured chordal bound eps = k x model tolerance.
/// It then compares classed loci with native and adjudicates differences with direct ray/Brep intersections.
/// </summary>
internal static class CurvedEngine
{
    private sealed record Input(GeometryBase Geometry, string Id, bool KernelEdge);
    private sealed record Body(Brep Brep, BoundingBox Box, string Id);

    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, double timeout)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ViewDrawing)solve.Invoke(null, new object[] { scene, sources, view, settings, limit.Token })!;
    }

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, ViewRequest view, string stem, string output, string[] experiments, double timeout)
    {
        var tol = scene.Tolerance; var rows = new List<object>();
        // PERFF_CE_ONLY=<text>: keep only sources whose identity contains the text (debugging one definition).
        if (Environment.GetEnvironmentVariable("PERFF_CE_ONLY") is { Length: > 0 } only) prepared = prepared.Where(p => p.Identity.Contains(only, StringComparison.Ordinal)).ToArray();
        void Save() => File.WriteAllText(Path.Combine(output, stem + "-ce.json"), JsonSerializer.Serialize(new
        { view = view.Name, tolerance = tol, rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount, rows }, new JsonSerializerOptions { WriteIndented = true }));

        // ---- Native reference. Cold run discarded; then the shipped default (ray audit on) and raw native (audit off).
        using (var cold = SolveView(scene, prepared, view, new NativeSettings(), timeout)) { }
        var busy = Spike.Busy.Start(); var timer = Stopwatch.StartNew();
        using var shipped = SolveView(scene, prepared, view, new NativeSettings(), timeout);
        double shippedWall = timer.Elapsed.TotalSeconds; var shippedBusy = busy.Stop();
        var inputs = new List<Input>(); int calls = 0;
        var settings = new NativeSettings
        {
            Verification = new(MaxSamples: 0),
            ObserveNativeInputs = list => { if (calls++ != 0) return; foreach (var i in list) inputs.Add(new(i.Geometry.Duplicate(), i.Source.Identity, i.KernelCandidate)); }
        };
        busy = Spike.Busy.Start(); timer.Restart();
        using var native = SolveView(scene, prepared, view, settings, timeout);
        double nativeWall = timer.Elapsed.TotalSeconds; var nativeBusy = busy.Stop();
        var plane = native.Frame.Plane;
        string Lengths(ViewDrawing d) => string.Join(", ", d.Items.GroupBy(p => p.Class).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()} pcs {g.Sum(p => { using var c = p.CopyGeometry(); return c is Curve k ? k.GetLength() : 0; }):F1} m"));
        Console.WriteLine($"--- {stem} [native shipped, audit on] wall {shippedWall:F3}s native solve {shipped.Phases.Seconds.GetValueOrDefault("native solve"):F3}s; {Lengths(shipped)}; busy {shippedBusy}");
        Console.WriteLine($"--- {stem} [native raw, audit off] wall {nativeWall:F3}s native solve {native.Phases.Seconds.GetValueOrDefault("native solve"):F3}s; {Lengths(native)}; busy {nativeBusy}; route {native.Report.Route}; error {native.Error}");
        rows.Add(new { name = "native", shippedWall, shippedNativeSolve = shipped.Phases.Seconds.GetValueOrDefault("native solve"), shippedLengths = Lengths(shipped), shippedBusy,
            nativeWall, nativeSolve = native.Phases.Seconds.GetValueOrDefault("native solve"), nativeLengths = Lengths(native), nativeBusy, inputs = inputs.Count, kernelEdges = inputs.Count(i => i.KernelEdge) });
        Save();

        int adjudicate = 250; bool tier1 = !experiments.Contains("t1off");
        foreach (var e in experiments) if (e.StartsWith("adjudicate:", StringComparison.Ordinal)) adjudicate = int.Parse(e[11..]);
        foreach (var experiment in experiments.Where(e => e.StartsWith("eps:", StringComparison.Ordinal)))
        {
            double multiple = double.Parse(experiment[4..], System.Globalization.CultureInfo.InvariantCulture);
            var row = Engine(inputs, plane, view, tol, multiple, tier1, native, stem, output, adjudicate, nativeWall);
            rows.Add(row); Save();
        }
        foreach (var i in inputs) i.Geometry.Dispose();
    }

    private sealed class Candidate { public Curve Curve = null!; public int Source; public int F0 = -1, F1 = -1; public bool Tangent; public int First, Count; }

    private static object Engine(List<Input> inputs, Plane plane, ViewRequest view, double tol, double multiple, bool tier1, ViewDrawing native, string stem, string output, int adjudicate, double nativeWall)
    {
        double eps = tol * multiple, angle = RhinoMath.DefaultAngleTolerance, sag = tol / 2, hMax = .5, hMin = Math.Max(eps / 2, 2 * tol);
        var e = plane.ZAxis; var toView = Transform.PlaneToPlane(plane, Plane.WorldXY);
        V3 Project(Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
        V3 Direction(Vector3d d) => new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis);
        Point3d World(V3 p) => plane.Origin + plane.XAxis * p.X + plane.YAxis * p.Y + plane.ZAxis * p.Z;

        var busy = Spike.Busy.Start();
        var phase = new Dictionary<string, double>(); var clock = Stopwatch.StartNew();
        void Lap(string name) { phase[name] = phase.GetValueOrDefault(name) + clock.Elapsed.TotalSeconds; clock.Restart(); }

        var items = inputs.Where(i => !i.KernelEdge).ToArray();
        var breps = new Brep?[items.Length]; var candidates = new List<Candidate>(); var chords = new List<Chord>();
        var solids = new List<Solid>(); var sheets = new List<SheetMesh>(); var bodies = new List<Body>();
        var unsupported = new List<string>(); var kinds = new Dictionary<string, int>();
        long triangles = 0, oracle = 0; int spanMixed = 0, spanTurn = 0; int chordChecks = 0, chordOver = 0, kinks = 0; double worstRatio = 0;
        var mode = Environment.GetEnvironmentVariable("PERFF_CE_CAND") ?? "edges";
        MeshIndex.StrictChain = Environment.GetEnvironmentVariable("PERFF_CE_CHAIN") switch { "strict" => 1, "loose" => 2, _ => 0 };
        bool closedForm = Environment.GetEnvironmentVariable("PERFF_CE_RHINO_SILHOUETTES") != "1"; var shapes = new List<Shape>(); bool closedFormOutlines = closedForm;
        var notes = Environment.GetEnvironmentVariable("PERFF_CE_TRACE") is not null ? new Dictionary<int, string>() : null;
        // Rhino's mesher does not hold Tolerance by itself (curved spike, finding A): ask for half and measure what came back.
        var mp = new MeshingParameters { Tolerance = eps / 2, RelativeTolerance = 0, MinimumEdgeLength = 0, MaximumEdgeLength = 0, GridAngle = RhinoMath.ToRadians(20), GridAspectRatio = 6,
            GridAmplification = 1, GridMinCount = 16, GridMaxCount = 0, RefineAngle = 0, RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true };

        for (int index = 0; index < items.Length; index++)
        {
            var (geometry, id, _) = items[index];
            clock.Restart();
            if (geometry is Curve free)
            {
                var c = new Candidate { Curve = free.DuplicateCurve(), Source = index }; candidates.Add(c);
                kinds["curve"] = kinds.GetValueOrDefault("curve") + 1; continue;
            }
            Brep? brep = geometry switch { Brep b => b, Extrusion x => x.ToBrep(), SubD s => s.ToBrep(), Surface s => s.ToBrep(), _ => null };
            if (brep is null) { unsupported.Add(id); kinds["unsupported " + geometry.GetType().Name] = kinds.GetValueOrDefault("unsupported " + geometry.GetType().Name) + 1; continue; }
            if (mode == "kink")
            {   // Faces become G1: a crease inside a face turns into an edge. Same point set.
                var split = brep.DuplicateBrep();
                if (split.Faces.SplitKinkyFaces(angle, true) && split.IsValid && split.Faces.Count != brep.Faces.Count) { if (!ReferenceEquals(brep, geometry)) brep.Dispose(); brep = split; kinks++; }
                else split.Dispose();
            }
            breps[index] = brep; bodies.Add(new(brep, brep.GetBoundingBox(toView), id));
            Lap("convert");

            // ---- Tier 1 recognition
            int[] tags = new int[brep.Faces.Count]; int solid = -1;
            if (tier1 && Recognise(brep, tol, toView, Project, Direction, tags, out var shapeFound) is { } recognised) { solid = solids.Count; solids.Add(recognised); shapes.Add(shapeFound!); kinds[recognised.GetType().Name] = kinds.GetValueOrDefault(recognised.GetType().Name) + 1; }
            else kinds["mesh"] = kinds.GetValueOrDefault("mesh") + 1;
            Lap("recognise");

            // ---- Candidates: creases and naked edges, as native does with tangent edges and seams off
            int firstCandidate = candidates.Count;
            foreach (var edge in brep.Edges)
            {
                var adjacent = edge.AdjacentFaces();
                bool crease = adjacent.Length == 2 && adjacent[0] != adjacent[1] && !edge.IsSmoothManifoldEdge(angle);
                bool naked = edge.Valence == EdgeAdjacency.Naked && adjacent.Length == 1;
                if (!crease && !naked && !(adjacent.Length > 2)) continue;
                candidates.Add(new Candidate { Curve = edge.DuplicateCurve(), Source = index, F0 = adjacent.Length > 0 ? adjacent[0] : -1, F1 = adjacent.Length > 1 ? adjacent[1] : -1 });
            }
            Lap("edges");
            bool outlines = solid >= 0 && closedForm && !shapes[^1].Sheet;
            var silhouettes = outlines ? null : Silhouette.Compute(brep, SilhouetteType.Tangent, view.Direction, tol, angle);
            if (outlines)
            {   // Closed-form outlines: a great circle for a sphere, two rulings per cylinder for a tube.
                var shape = shapes[^1];
                if (shape.Sphere is { } ball) candidates.Add(new Candidate { Curve = new ArcCurve(new Circle(new Plane(ball.Center, e), ball.Radius)), Source = index, F0 = shape.OuterFace, Tangent = true });
                else
                {
                    var side = Vector3d.CrossProduct(shape.Axis, e);
                    if (side.Unitize())
                        foreach (var (radius, face) in new[] { (shape.Outer, shape.OuterFace), (shape.Inner, shape.InnerFace) })
                        {
                            if (radius <= 0) continue;
                            foreach (int sign in new[] { -1, 1 })
                            {
                                var start = shape.Origin + side * (sign * radius); var ends = new Point3d[2];
                                for (int k = 0; k < 2; k++) { var n = shape.Caps[k].Normal; ends[k] = start + shape.Axis * (((shape.Caps[k].Origin - start) * n) / (shape.Axis * n)); }
                                candidates.Add(new Candidate { Curve = new LineCurve(ends[0], ends[1]), Source = index, F0 = face, Tangent = true });
                            }
                        }
                }
            }
            if (silhouettes is not null)
                foreach (var s in silhouettes)
                {
                    if (s.Curve is not { } curve) continue;
                    if (curve.GetLength() <= tol) { curve.Dispose(); continue; }
                    int face = s.GeometryComponentIndex.ComponentIndexType == ComponentIndexType.BrepFace ? s.GeometryComponentIndex.Index : -1;
                    if (face < 0)
                    {   // find the face the silhouette lies on
                        var mid = curve.PointAt(curve.Domain.Mid); double best = 4 * tol;
                        foreach (var f in brep.Faces) if (f.ClosestPoint(mid, out var u, out var v) && f.PointAt(u, v).DistanceTo(mid) < best && f.IsPointOnFace(u, v) != PointFaceRelation.Exterior) { best = f.PointAt(u, v).DistanceTo(mid); face = f.FaceIndex; }
                    }
                    candidates.Add(new Candidate { Curve = curve, Source = index, F0 = face, Tangent = true });
                }
            Lap("silhouettes");

            // ---- Chords, with what is known about the candidate's own surface
            var faceKinds = solid >= 0 ? Array.Empty<FaceKind>() : brep.Faces.Select(f => Kind(f, tol)).ToArray();
            for (int ci = firstCandidate; ci < candidates.Count; ci++)
            {
                var c = candidates[ci]; c.First = chords.Count;
                double reachZ = bodies[^1].Box.Max.Z;
                bool x0 = c.F0 < 0 || solid >= 0 || Exact(faceKinds[c.F0]), x1 = c.F1 < 0 || solid >= 0 || Exact(faceKinds[c.F1]);
                // Own-face limits are evaluated at stations at most 50 mm apart along the candidate, and chords never span a
                // station. Between two stations the limit is the smaller one. Where one station sees a near crossing and
                // the other none, the crossing depth passes through zero in between (a tangential change point): undecidable.
                var cuts = new SortedSet<double> { c.Curve.Domain.Min, c.Curve.Domain.Max };
                for (int k = 0; k < c.Curve.SpanCount; k++) { var d = c.Curve.SpanDomain(k); cuts.Add(d.Min); cuts.Add(d.Max); }
                if (solid < 0) foreach (double t in c.Curve.DivideByLength(.05, false) ?? Array.Empty<double>()) cuts.Add(t);
                var stations = cuts.Where(t => t >= c.Curve.Domain.Min && t <= c.Curve.Domain.Max).ToArray();
                var limits = new ((double Limit, bool Crossing, double Facing), (double Limit, bool Crossing, double Facing))[solid < 0 ? stations.Length : 0];
                for (int k = 0; k < limits.Length; k++)
                {
                    var q = c.Curve.PointAt(stations[k]); double reach = reachZ - Project(q).Z + 1e-3;
                    limits[k] = (c.F0 >= 0 ? Clearance(brep.Faces[c.F0], faceKinds[c.F0], q, e, tol, c.Tangent, reach, ref oracle) : (double.NegativeInfinity, false, double.NaN),
                                 c.F1 >= 0 ? Clearance(brep.Faces[c.F1], faceKinds[c.F1], q, e, tol, c.Tangent, reach, ref oracle) : (double.NegativeInfinity, false, double.NaN));
                }
                bool edgeCandidate = !c.Tangent;
                double Between((double Limit, bool Crossing, double Facing) p, (double Limit, bool Crossing, double Facing) q)
                {
                    // One station sees a crossing, the other none: the crossing depth passes through zero (or leaves) in between.
                    if (p.Crossing != q.Crossing) { spanMixed++; return double.NegativeInfinity; }
                    // The face turns from facing the viewer to facing away along an edge: a crossing is born at depth zero there.
                    if (edgeCandidate && !double.IsNaN(p.Facing) && !double.IsNaN(q.Facing) && (p.Facing * q.Facing <= 0 || Math.Min(Math.Abs(p.Facing), Math.Abs(q.Facing)) < .02)) { spanTurn++; return double.NegativeInfinity; }
                    return Math.Min(p.Limit, q.Limit);
                }
                for (int k = 0; k + 1 < stations.Length; k++)
                    foreach (var (ta, tb) in Subdivide(c.Curve, stations[k], stations[k + 1], sag, hMax))
                    {
                        var chord = new Chord { A = Project(c.Curve.PointAt(ta)), M = Project(c.Curve.PointAt((ta + tb) / 2)), B = Project(c.Curve.PointAt(tb)), Sheet = solid >= 0 ? -1 : index, F0 = c.F0, F1 = c.F1, Solid = solid, Tangent = c.Tangent,
                            OnSurface = (c.F0 >= 0 ? tags[c.F0] : 0) | (c.F1 >= 0 ? tags[c.F1] : 0), S0 = double.NegativeInfinity, S1 = double.NegativeInfinity, X0 = x0, X1 = x1 };
                        if (solid < 0) { chord.S0 = Between(limits[k].Item1, limits[k + 1].Item1); chord.S1 = Between(limits[k].Item2, limits[k + 1].Item2); }
                        if (notes is not null && solid < 0)
                        {
                            Point3d qa = c.Curve.PointAt(stations[k]), qb = c.Curve.PointAt(stations[k + 1]);
                            notes[chords.Count] = $"stations t {stations[k]:G6}..{stations[k + 1]:G6} limits {limits[k]} {limits[k + 1]} reachZ {reachZ:F3} zA {Project(qa).Z:F3} zB {Project(qb).Z:F3} A {qa} B {qb}";
                        }
                        chords.Add(chord);
                    }
                c.Count = chords.Count - c.First;
            }
            Lap("chords + local analysis");

            // ---- Tier 2 occluder: a welded mesh with a measured chordal bound
            if (solid >= 0) continue;
            var meshes = Mesh.CreateFromBrep(brep, mp);
            Lap("meshing");
            if (meshes is null || meshes.Length != brep.Faces.Count) { unsupported.Add(id); continue; }
            var lookup = new Dictionary<Point3d, int>(); var vertices = new List<V3>(); var tris = new List<int>(); var faces = new List<int>(); double worst = 0;
            for (int f = 0; f < meshes.Length; f++)
            {
                var mesh = meshes[f]; mesh.Faces.ConvertQuadsToTriangles();
                var map = new int[mesh.Vertices.Count];
                for (int v = 0; v < map.Length; v++)
                {
                    var p = mesh.Vertices.Point3dAt(v);
                    if (!lookup.TryGetValue(p, out var w)) { w = vertices.Count; vertices.Add(Project(p)); lookup.Add(p, w); }
                    map[v] = w;
                }
                int count = mesh.Faces.Count, stride = Math.Max(1, count / 64); var face = brep.Faces[f]; bool planar = face.IsPlanar(tol);
                for (int t = 0; t < count; t++)
                {
                    var mf = mesh.Faces[t]; int a = map[mf.A], b = map[mf.B], c = map[mf.C];
                    if (a == b || b == c || a == c) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c); faces.Add(f);
                    if (planar || t % stride != 0) continue;
                    Point3d pa = mesh.Vertices.Point3dAt(mf.A), pb = mesh.Vertices.Point3dAt(mf.B), pc = mesh.Vertices.Point3dAt(mf.C);
                    foreach (var q in new[] { (pa + pb + pc) / 3, (pa + pb) / 2, (pb + pc) / 2, (pa + pc) / 2 })
                    {
                        if (!face.ClosestPoint(q, out var u, out var v)) continue;
                        double d = face.PointAt(u, v).DistanceTo(q); worst = Math.Max(worst, d); chordChecks++; if (d > eps) chordOver++;
                    }
                }
                mesh.Dispose();
            }
            worstRatio = Math.Max(worstRatio, worst / eps);
            // The bound used for this sheet is what was measured (with a margin), never the request alone.
            var sheet = new SheetMesh { Sheet = index, Eps = Math.Max(eps, 1.25 * worst), Vertices = vertices.ToArray(), Triangles = tris.ToArray(), Faces = faces.ToArray() };
            sheets.Add(sheet); triangles += faces.Count;
            Lap("weld + chord check");
        }
        // Free curves: chords only.
        clock.Restart();
        foreach (var c in candidates.Where(c => c.Count == 0 && items[c.Source].Geometry is Curve))
        {
            c.First = chords.Count;
            for (int k = 0; k < c.Curve.SpanCount; k++)
                foreach (var (ta, tb) in Subdivide(c.Curve, c.Curve.SpanDomain(k).Min, c.Curve.SpanDomain(k).Max, sag, hMax))
                    chords.Add(new Chord { A = Project(c.Curve.PointAt(ta)), M = Project(c.Curve.PointAt((ta + tb) / 2)), B = Project(c.Curve.PointAt(tb)), Sheet = -1, F0 = -1, F1 = -1, Solid = -1 });
            c.Count = chords.Count - c.First;
        }
        Lap("chords + local analysis");

        // ---- Managed solve
        var solidIndex = new SolidIndex(solids.ToArray()); var meshIndex = new MeshIndex(sheets);
        Lap("index");
        var chordArray = chords.ToArray(); var pieces = new List<Piece>[chordArray.Length];
        int threads = Math.Min(Environment.ProcessorCount, 8); long meshTests = 0, solidTests = 0, nodes = 0;
        Parallel.For(0, chordArray.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, () => new ChordSolver.Scratch(meshIndex), (i, _, scratch) =>
        {
            var list = new List<Piece>(2); ChordSolver.Solve(chordArray[i], solidIndex, meshIndex, tol, hMin, scratch, list); pieces[i] = list; return scratch;
        }, scratch => { Interlocked.Add(ref meshTests, scratch.Mesh.Tests); Interlocked.Add(ref solidTests, scratch.SolidTests); Interlocked.Add(ref nodes, scratch.Mesh.Nodes); });
        Lap("solve");
        double engineSeconds = phase.Values.Sum(); var engineBusy = busy.Stop();

        // ---- Output, lengths and loci
        double step = .02, strict = 10 * tol, loose = .025;
        var engineLocus = new Locus(.05); var nativeLocus = new Locus(.05);
        var length = new double[3]; var why = new double[5]; var dump = new List<object>(); var unresolvedWhy = new SortedDictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < chordArray.Length; i++)
            foreach (var p in pieces[i])
            {
                var a = chordArray[i].At(p.T0); var b = chordArray[i].At(p.T1); double l = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (p.T1 - p.T0 <= 1e-9) continue;
                length[(int)p.Class] += l; why[p.Why] += l; engineLocus.Add(a.X, a.Y, b.X, b.Y, (byte)p.Class);
                if (p.Class == Cls.Unresolved)
                {
                    var ch = chordArray[i]; bool noLimit = ch.F0 >= 0 && double.IsNegativeInfinity(ch.S0) || ch.F1 >= 0 && double.IsNegativeInfinity(ch.S1);
                    string key = p.Why == 4 ? "near another object" : (ch.Tangent ? "silhouette" : "edge") + (noLimit ? ", own face undecidable" : ", own mesh too coarse here");
                    unresolvedWhy[key] = unresolvedWhy.GetValueOrDefault(key) + l;
                }
                dump.Add(new { c = p.Class.ToString(), w = p.Why, p = new[] { a.X, a.Y, b.X, b.Y } });
            }
        var nativeLength = new double[3]; var nativePieces = new List<(byte Class, double[] P)>();
        foreach (var item in native.Items)
        {
            using var g = item.CopyGeometry(); if (g is not Curve curve) continue;
            byte cls = item.Class == LineClass.Hidden ? (byte)1 : item.Class == LineClass.Unresolved ? (byte)2 : (byte)0;
            Polyline? polyline;
            if (!curve.TryGetPolyline(out polyline))
            {
                using var approximation = curve.ToPolyline(tol / 8, .02, 0, 0);
                if (approximation is null || !approximation.TryGetPolyline(out polyline)) polyline = new Polyline(new[] { curve.PointAtStart, curve.PointAtEnd });
            }
            var flat = new double[polyline.Count * 2];
            for (int k = 0; k < polyline.Count; k++) { var v = Project(polyline[k]); flat[2 * k] = v.X; flat[2 * k + 1] = v.Y; }
            for (int k = 1; k < polyline.Count; k++)
            {
                nativeLocus.Add(flat[2 * k - 2], flat[2 * k - 1], flat[2 * k], flat[2 * k + 1], cls);
                nativeLength[cls] += Math.Sqrt(Math.Pow(flat[2 * k] - flat[2 * k - 2], 2) + Math.Pow(flat[2 * k + 1] - flat[2 * k - 1], 2));
            }
            nativePieces.Add((cls, flat));
        }
        static int Drawn(int mask) => (mask & 4) != 0 ? 2 : (mask & 1) != 0 ? 0 : (mask & 2) != 0 ? 1 : 3; // Unresolved > Visible > Hidden > nothing

        // Engine -> native. matrix[engine class, native drawn class or 3 = no native line]; certain engine classes are then split.
        var matrix = new double[3, 4]; double agree = 0, shift = 0, core = 0, nativeUnresolved = 0, engineOnly = 0, shadowed = 0;
        var samples = new List<(int Chord, double T, double Half, int Engine, int Native, string Kind, double Piece)>(); var random = new Random(20261115);
        for (int i = 0; i < chordArray.Length; i++)
            foreach (var p in pieces[i])
            {
                var a = chordArray[i].At(p.T0); var b = chordArray[i].At(p.T1); double l = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (p.T1 - p.T0 <= 1e-9) continue;
                int n = Math.Max(1, (int)Math.Ceiling(l / step)); double weight = l / n, chordLength = (chordArray[i].B - chordArray[i].A).Length;
                for (int k = 0; k < n; k++)
                {
                    double t = p.T0 + (p.T1 - p.T0) * (k + .5) / n; var q = chordArray[i].At(t);
                    int mine = Drawn(engineLocus.Classes(q.X, q.Y, tol));
                    if (mine != (int)p.Class) { shadowed += weight; continue; }   // a coincident line of a higher class is what is drawn here
                    int mask = nativeLocus.Classes(q.X, q.Y, strict), theirs = Drawn(mask);
                    matrix[mine, theirs] += weight;
                    if (mine == 2) continue;
                    double half = chordLength <= 0 ? 0 : Math.Min(.002, l / 4) / chordLength; string kind;
                    if (theirs == 3) { engineOnly += weight; kind = "engine-only"; }
                    else if (theirs == mine) { agree += weight; kind = "agree"; }
                    else if (theirs == 2) { nativeUnresolved += weight; kind = "native-unresolved"; }
                    else if ((nativeLocus.Classes(q.X, q.Y, loose) & (1 << mine)) != 0) { shift += weight; kind = "shift"; }
                    else { core += weight; kind = "core"; }
                    samples.Add((i, t, half, mine, theirs, kind, l));
                }
            }
        // Native -> engine.
        var reverse = new double[3, 4];
        foreach (var (cls, flat) in nativePieces)
            for (int k = 2; k < flat.Length; k += 2)
            {
                double ax = flat[k - 2], ay = flat[k - 1], bx = flat[k], by = flat[k + 1], l = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int n = Math.Max(1, (int)Math.Ceiling(l / step));
                for (int j = 0; j < n; j++)
                {
                    double x = ax + (bx - ax) * (j + .5) / n, y = ay + (by - ay) * (j + .5) / n;
                    if (Drawn(nativeLocus.Classes(x, y, tol)) != cls) continue;
                    reverse[cls, Drawn(engineLocus.Classes(x, y, strict))] += l / n;
                }
            }
        Lap("compare");

        // ---- Independent adjudication: direct ray / Brep intersections on the original geometry.
        double far = bodies.Count == 0 ? 1 : bodies.Max(b => b.Box.Max.Z) - bodies.Min(b => b.Box.Min.Z) + 10;
        long rays = 0;
        // Does anything lie more than `from` in front of the point? null = a hit too close to call.
        bool? Blocked(Point3d origin, double from, double ambiguous, int only = -1, int except = -1, double precision = 0)
        {
            var local = origin; local.Transform(toView); bool unsure = false; if (precision <= 0) precision = tol / 10;
            using var ray = new LineCurve(origin + e * from, origin + e * (far + (bodies.Max(b => b.Box.Max.Z) - local.Z)));
            for (int bi = 0; bi < bodies.Count; bi++)
            {
                if (only >= 0 && bi != only || bi == except) continue;
                var body = bodies[bi]; var box = body.Box;
                if (local.X < box.Min.X - 8 * tol || local.X > box.Max.X + 8 * tol || local.Y < box.Min.Y - 8 * tol || local.Y > box.Max.Y + 8 * tol || box.Max.Z < local.Z + from) continue;
                rays++;
                if (!Intersection.CurveBrep(ray, body.Brep, precision, out var overlaps, out var hits)) { unsure = true; continue; }
                double gap = double.NegativeInfinity;
                foreach (var h in hits) gap = Math.Max(gap, (h - origin) * e);
                // The ray running along a surface for a stretch is contact, a matter of the tie convention: not judged.
                foreach (var o in overlaps) { double s0 = (o.PointAtStart - origin) * e, s1 = (o.PointAtEnd - origin) * e; o.Dispose(); if (Math.Abs(s1 - s0) > 4 * tol) unsure = true; else gap = Math.Max(gap, Math.Max(s0, s1)); }
                if (gap > ambiguous) return true;
                if (gap > from) unsure = true;
            }
            return unsure ? null : false;
        }
        // Which item each chord belongs to, and which checked body that item is.
        var chordItem = new int[chordArray.Length]; foreach (var c in candidates) for (int k = c.First; k < c.First + c.Count; k++) chordItem[k] = c.Source;
        var bodyOf = new int[items.Length]; Array.Fill(bodyOf, -1); for (int bi = 0; bi < bodies.Count; bi++) bodyOf[Array.FindIndex(items, x => ReferenceEquals(x.Id, bodies[bi].Id))] = bi;
        // 0 visible, 1 hidden, -1 inconclusive, at one point of a chord.
        int Verdict(int index, double t)
        {
            var chord = chordArray[index]; var q = chord.At(t); var p = World(q);
            if (!chord.Tangent)
            {
                var r = Blocked(p, tol, 20 * tol); return r is null ? -1 : r.Value ? 1 : 0;
            }
            // A tangent silhouette. Its own body: the ray grazes the surface, so step sideways in
            // the drawing, both ways; the point is visible when the ray on either side is clear of the body, hidden when both
            // sides are blocked. Two step sizes must agree. A closed-form outline is exact, so its steps can be far smaller.
            int own = bodyOf[chordItem[index]];
            var d = chord.B - chord.A; double l = Math.Sqrt(d.X * d.X + d.Y * d.Y); if (l == 0) return -1;
            var normal = plane.XAxis * (-d.Y / l) + plane.YAxis * (d.X / l); int? verdict = null; bool exact = chord.Solid >= 0 && closedFormOutlines;
            // Other bodies: hidden only if blocked on both sides of the curve, clear only if clear on both. One side
            // each is a body whose outline runs along this curve (aligned members): a tie, not judged.
            var otherLeft = Blocked(p + normal * (4 * tol), tol, 20 * tol, except: own); var otherRight = Blocked(p - normal * (4 * tol), tol, 20 * tol, except: own);
            if (otherLeft is null || otherRight is null || otherLeft.Value != otherRight.Value) return -1;
            if (otherLeft.Value) return 1;
            if (own < 0) return 0;
            foreach (double delta in exact ? new[] { .2 * tol, .4 * tol } : new[] { 4 * tol, 8 * tol })
            {
                var left = Blocked(p + normal * delta, tol, tol, only: own, precision: exact ? 1e-7 : 0); var right = Blocked(p - normal * delta, tol, tol, only: own, precision: exact ? 1e-7 : 0);
                if (left is null || right is null) return -1;
                int v = left.Value && right.Value ? 1 : 0;
                if (verdict is { } old && old != v) return -1; verdict = v;
            }
            return verdict ?? -1;
        }
        var adjudication = new SortedDictionary<string, int[]>(StringComparer.Ordinal); var wrong = new List<object>(); int traced = 0; var engineOnlyNotes = new List<string>();
        foreach (var group in samples.GroupBy(s => s.Kind + (s.Kind is "agree" or "engine-only" or "native-unresolved" ? "" : s.Engine == 0 ? " engine Visible / native Hidden" : " engine Hidden / native Visible")))
        {
            var chosen = group.OrderBy(_ => random.Next()).Take(adjudicate).ToArray(); var counts = new int[4]; // engine right, engine wrong, inconclusive, near a change point
            foreach (var s in chosen)
            {
                var chord = chordArray[s.Chord];
                // The check steps up to 0.8 mm sideways and 2 mm along the curve: it cannot judge a piece shorter than that.
                if (s.Piece < .005 && s.Kind != "agree") { counts[3]++; continue; }
                int v0 = Verdict(s.Chord, s.T), v1 = Verdict(s.Chord, s.T - s.Half), v2 = Verdict(s.Chord, s.T + s.Half);
                if (s.Kind == "engine-only" && engineOnlyNotes.Count < 12) { var q0 = chord.At(s.T); engineOnlyNotes.Add($"{items[chordItem[s.Chord]].Id[^12..]} {(chord.Solid >= 0 ? solids[chord.Solid].GetType().Name : "mesh")} {(chord.Tangent ? "silhouette" : "edge")} on {chord.OnSurface} faces {chord.F0}/{chord.F1} at ({q0.X:F3}, {q0.Y:F3}) verdict {v0}"); }
                if (v0 < 0 || v1 < 0 || v2 < 0) counts[2]++;
                else if (v0 != v1 || v0 != v2) counts[3]++;
                else if (v0 == s.Engine) counts[0]++;
                else
                {
                    if (notes is not null && traced++ < 4)
                    {
                        var scratch = new ChordSolver.Scratch(meshIndex); scratch.Mesh.Trace = new(); scratch.Mesh.TraceT = s.T; var again = new List<Piece>();
                        ChordSolver.Solve(chord, solidIndex, meshIndex, tol, hMin, scratch, again);
                        Console.WriteLine($"  TRACE {group.Key}: chord {s.Chord} t {s.T:F4} tangent {chord.Tangent} faces {chord.F0}/{chord.F1} clearance {chord.S0:G4}/{chord.S1:G4} sheet {chord.Sheet} A {chord.A} B {chord.B}; pieces {string.Join(" ", again.Select(p => $"[{p.T0:F3},{p.T1:F3} {p.Class}/{p.Why}]"))}");
                        foreach (var line in scratch.Mesh.Trace.TakeLast(Environment.GetEnvironmentVariable("PERFF_CE_TRACE") == "1" ? 40 : 0)) Console.WriteLine(line);
                        {
                            var owner = candidates.First(c => s.Chord >= c.First && s.Chord < c.First + c.Count); var body = breps[owner.Source]!; var wp = World(chord.At(s.T));
                            if (notes is not null && notes.TryGetValue(s.Chord, out var note)) Console.WriteLine("    " + note + $" | sample world {wp}");
                            if (notes is not null && chord.F0 >= 0)
                                foreach (double rayLength in new[] { .25, 1, 5 })
                                    foreach (double lift in new[] { 0, 1e-6 })
                                    {
                                        var station = owner.Curve.PointAt(owner.Curve.Domain.Min) + e * lift;
                                        using var shortRay = new LineCurve(station, station + e * rayLength);
                                        bool ok = Intersection.CurveBrepFace(shortRay, body.Faces[chord.F0], tol / 10, out var ov, out var pts);
                                        Console.WriteLine($"    station probe at curve start, ray {rayLength} m, lifted {lift}: {Hits(ok, ov, pts).Replace(wp.ToString(), "")} (depths measured from the sample, not the station)");
                                    }
                            using var probe = new LineCurve(wp, wp + e * 5);
                            string Hits(bool ok, Curve[] ov, Point3d[] pts) => !ok ? "failed" : string.Join(",", pts.Select(h => ((h - wp) * e).ToString("F4"))) + " | overlaps " + string.Join(",", ov.Select(o => $"[{(o.PointAtStart - wp) * e:F4},{(o.PointAtEnd - wp) * e:F4}]"));
                            foreach (int f in new[] { chord.F0, chord.F1 })
                                if (f >= 0) { bool ok = Intersection.CurveBrepFace(probe, body.Faces[f], tol / 10, out var ov, out var pts); Console.WriteLine($"    probe face {f} ({(body.Faces[f].IsPlanar(tol) ? "planar" : "curved")}): {Hits(ok, ov, pts)}"); }
                            { bool ok = Intersection.CurveBrep(probe, body, tol / 10, out var ov, out var pts); Console.WriteLine($"    probe whole body ({body.Faces.Count} faces): {Hits(ok, ov, pts)}"); }
                            for (int f = 0; f < body.Faces.Count; f++) { bool ok = Intersection.CurveBrepFace(probe, body.Faces[f], tol / 10, out var ov, out var pts); if (pts.Length + ov.Length > 0) Console.WriteLine($"    probe each face {f}: {Hits(ok, ov, pts)}"); }
                        }
                    }
                    counts[1]++; var q = chord.At(s.T); if (wrong.Count < 200) wrong.Add(new { group = group.Key, source = items[candidates.First(c => s.Chord >= c.First && s.Chord < c.First + c.Count).Source].Id, x = q.X, y = q.Y, z = q.Z, engine = s.Engine, native = s.Native, chord.Tangent, chord.Solid, chord.F0, chord.F1, t = s.T }); }
            }
            adjudication[group.Key] = new[] { group.Count(), chosen.Length, counts[0], counts[1], counts[2], counts[3] };
        }
        Lap("adjudicate");

        var name = $"{stem}-ce{multiple:G}{(tier1 ? "" : "-t1off")}";
        File.WriteAllText(Path.Combine(output, name + "-engine.json"), JsonSerializer.Serialize(new { tolerance = tol, planar2d = true, pieces = dump }));
        File.WriteAllText(Path.Combine(output, name + "-native.json"), JsonSerializer.Serialize(new { tolerance = tol, planar2d = true, pieces = nativePieces.Select(p => new { c = p.Class == 0 ? "Visible" : p.Class == 1 ? "Hidden" : "Unresolved", p = p.P }) }));
        for (int i = 0; i < breps.Length; i++) if (breps[i] is { } owned && !ReferenceEquals(owned, items[i].Geometry)) owned.Dispose();
        foreach (var c in candidates) c.Curve.Dispose();

        string Row(double[,] m, int r) => $"{m[r, 0]:F2} / {m[r, 1]:F2} / {m[r, 2]:F2} / {m[r, 3]:F2}";
        double certain = agree + shift + core + nativeUnresolved + engineOnly;
        Console.WriteLine($"=== {stem} [engine eps {eps:G4} = {multiple:G} x tol{(tier1 ? "" : ", tier 1 off")}] {engineSeconds:F3}s vs native {nativeWall:F3}s ({nativeWall / engineSeconds:F1}x); threads {threads}; busy {engineBusy}");
        Console.WriteLine($"    phases: {string.Join(", ", phase.Where(p => p.Key is not ("compare" or "adjudicate")).Select(p => $"{p.Key} {p.Value:F3}"))} | compare {phase.GetValueOrDefault("compare"):F2} adjudicate {phase.GetValueOrDefault("adjudicate"):F2}");
        Console.WriteLine($"    sources {items.Length} (candidate mode {mode}, {kinks} split at kinks, chain {(MeshIndex.StrictChain == 1 ? "strict" : MeshIndex.StrictChain == 2 ? "loose" : "default")}): {string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))}; unsupported {unsupported.Count}; candidates {candidates.Count} ({candidates.Count(c => c.Tangent)} silhouettes); chords {chordArray.Length}; triangles {triangles}; measured chord deviation worst {worstRatio:F2} eps, {chordOver}/{chordChecks} checks over eps; mesh tests {meshTests}, nodes {nodes}, solid tests {solidTests}, own-face ray tests {oracle}");
        Console.WriteLine($"    engine length m: Visible {length[0]:F1} Hidden {length[1]:F1} Unresolved {length[2]:F1} ({length[2] / Math.Max(1e-9, length.Sum()):P2} of all, {length[2] / Math.Max(1e-9, length[0] + length[2]):P2} of non-hidden); by reason: exact hidden {why[0]:F1}, mesh hidden {why[1]:F1}, visible {why[2]:F1}, unresolved own {why[3]:F1}, unresolved other {why[4]:F1}");
        Console.WriteLine($"    unresolved m: {string.Join("; ", unresolvedWhy.Select(u => $"{u.Key} {u.Value:F1}"))}");
        lock (Refusals) { Console.WriteLine($"    own-face tests refused: {string.Join("; ", Refusals.Select(r => $"{r.Key} {r.Value}"))}; spans refused between stations: crossing appears {spanMixed}, face turns away {spanTurn}"); Refusals.Clear(); }
        Console.WriteLine($"    native length m: Visible {nativeLength[0]:F1} Hidden {nativeLength[1]:F1} Unresolved {nativeLength[2]:F1}");
        Console.WriteLine($"    engine drawn -> native within {strict * 1000:G3} mm (Visible / Hidden / Unresolved / no line): V {Row(matrix, 0)} | H {Row(matrix, 1)} | U {Row(matrix, 2)}; shadowed duplicates {shadowed:F1}");
        Console.WriteLine($"    native drawn -> engine within {strict * 1000:G3} mm (Visible / Hidden / Unresolved / no line): V {Row(reverse, 0)} | H {Row(reverse, 1)} | U {Row(reverse, 2)}");
        Console.WriteLine($"    engine certain {certain:F1} m: same class {agree:F1} ({agree / Math.Max(1e-9, certain):P2}), native Unresolved {nativeUnresolved:F1}, class differs but within {loose * 1000:G3} mm of the same class (change point) {shift:F2}, differs {core:F2}, no native line {engineOnly:F2}");
        foreach (var (key, v) in adjudication) Console.WriteLine($"    adjudication [{key}]: {v[0]} samples, checked {v[1]}: engine right {v[2]}, engine wrong {v[3]}, inconclusive {v[4]}, at a change point {v[5]}");
        Console.WriteLine($"    rays {rays}");
        if (engineOnlyNotes.Count > 0) Console.WriteLine("    engine lines with no native line, examples: " + string.Join(" | ", engineOnlyNotes));
        return new
        {
            name = "engine", multiple, eps, tier1, engineSeconds, nativeWall, speedup = nativeWall / engineSeconds, threads, engineBusy, phases = phase, sources = items.Length, kinds, unsupported, candidates = candidates.Count,
            silhouettes = candidates.Count(c => c.Tangent), chords = chordArray.Length, triangles, worstRatio, chordOver, chordChecks, meshTests, nodes, solidTests, ownFaceRayTests = oracle, mode, kinks,
            length = new { visible = length[0], hidden = length[1], unresolved = length[2] }, reasons = new { exactHidden = why[0], meshHidden = why[1], visible = why[2], unresolvedOwn = why[3], unresolvedOther = why[4] },
            unresolvedWhy, nativeLength = new { visible = nativeLength[0], hidden = nativeLength[1], unresolved = nativeLength[2] },
            engineToNative = Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 4).Select(c => matrix[r, c]).ToArray()).ToArray(),
            nativeToEngine = Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 4).Select(c => reverse[r, c]).ToArray()).ToArray(),
            shadowed, certain, agree, nativeUnresolved, shift, core, engineOnly, adjudication, wrong, engineOnlyNotes, rays, strict, loose
        };
    }

    /// <summary>Parameter intervals whose chord stays within sag of the curve (checked at the quarter points) and is at most hMax long.</summary>
    private static IEnumerable<(double, double)> Subdivide(Curve curve, double from, double to, double sag, double hMax)
    {
        var stack = new Stack<(double A, double B, int Depth)>();
        if (to > from) stack.Push((from, to, 0));
        while (stack.Count > 0)
        {
            var (a, b, depth) = stack.Pop();
            Point3d pa = curve.PointAt(a), pb = curve.PointAt(b); var line = new Line(pa, pb); double length = line.Length; bool fine = length <= hMax;
            if (fine && depth < 40)
                foreach (double k in new[] { .5, .25, .75 })
                {
                    var p = curve.PointAt(a + (b - a) * k);
                    double d = length <= 0 ? p.DistanceTo(pa) : line.DistanceTo(p, true);
                    if (d > sag) { fine = false; break; }
                }
            if (fine || depth >= 40) { if (length > 0 || depth >= 40) yield return (a, b); else if (curve.PointAt((a + b) / 2).DistanceTo(pa) > 0) { stack.Push(((a + b) / 2, b, depth + 1)); stack.Push((a, (a + b) / 2, depth + 1)); } continue; }
            stack.Push(((a + b) / 2, b, depth + 1)); stack.Push((a, (a + b) / 2, depth + 1));
        }
    }

    /// <summary>
    /// Depth along the view ray from a point of a candidate within which one of its own faces is not crossed again.
    ///   plane                 never crossed again.
    ///   cylinder, sphere      the other root of the ray with the untrimmed surface (closed form).
    ///   anything else         the first intersection of the ray with the face itself (Rhino's line/face intersection,
    ///                         standing in for a certified ray/NURBS test). A candidate is only known to tol, so a hit
    ///                         inside the "grazing" depth that such an error can produce is not evidence: on a tangent
    ///                         silhouette it is the tangency itself and is ignored (unless the view direction is nearly
    ///                         asymptotic there); on an edge it makes the piece undecidable (limit = -infinity).
    /// For a plane, cylinder or sphere the result holds for the whole face at any depth. Otherwise it is sampled at
    /// stations at most 50 mm apart along the candidate and reaches LocalDepth at most (Facing = n·e there, so that a
    /// span where the face turns away from the viewer can be refused); the core then exempts only
    /// triangles of that face that are depth-connected to the candidate and nearer than the limit.
    /// </summary>
    private static readonly SortedDictionary<string, int> Refusals = new(StringComparer.Ordinal);
    private static (double Limit, bool Crossing, double Facing) Clearance(BrepFace face, FaceKind kind, Point3d point, Vector3d e, double tol, bool tangentCandidate, double reach, ref long oracle)
    {
        reach = Math.Min(reach, LocalDepth); (double, bool, double) No(string why) { lock (Refusals) Refusals[why] = Refusals.GetValueOrDefault(why) + 1; return (double.NegativeInfinity, false, double.NaN); }
        if (kind.Planar) return (double.PositiveInfinity, false, double.NaN);
        if (kind.Cylinder is { } cylinder)
        {
            var u = cylinder.Axis; u.Unitize(); var w = point - cylinder.Center; double eu = e * u, wu = w * u, a = 1 - eu * eu, b = w * e - wu * eu;
            if (a < 1e-12) return (double.PositiveInfinity, false, double.NaN);
            double other = -2 * b / a; return other <= tol ? (double.PositiveInfinity, false, double.NaN) : (other * (1 - 1e-9) - tol, true, double.NaN);
        }
        if (kind.Sphere is { } sphere)
        {
            double other = -2 * ((point - sphere.Center) * e); return other <= tol ? (double.PositiveInfinity, false, double.NaN) : (other * (1 - 1e-9) - tol, true, double.NaN);
        }
        if (reach <= tol) return (double.PositiveInfinity, false, double.NaN);
        if (!face.ClosestPoint(point, out var su, out var sv)) return No("no closest point");
        var curvature = face.CurvatureAt(su, sv); if (curvature is null) return No("no curvature");
        var n = curvature.Normal; double k1 = curvature.Kappa(0), k2 = curvature.Kappa(1), kmax = Math.Max(Math.Abs(k1), Math.Abs(k2)), ne = n * e;
        var tangent = e - n * ne; double kd = 0;
        if (tangent.Unitize()) { double c = tangent * curvature.Direction(0), s = tangent * curvature.Direction(1); kd = Math.Abs(k1 * c * c + k2 * s * s); }
        double graze;
        if (tangentCandidate)
        {
            if (kmax <= 1e-9 || kd < .02 * kmax) return No(kmax <= 1e-9 ? "silhouette on a flat spot" : "silhouette, view direction nearly asymptotic");
            graze = 4 * Math.Sqrt(2 * tol / kd);
        }
        else
        {
            graze = Math.Min(Math.Abs(ne) > 1e-9 ? 2 * tol / Math.Abs(ne) : double.PositiveInfinity, kd > 1e-9 ? 4 * Math.Sqrt(2 * tol / kd) : double.PositiveInfinity);
            if (double.IsInfinity(graze)) return No("edge, ray in a flat tangent direction");
        }
        oracle++;
        using var ray = new LineCurve(point, point + e * reach);
        if (!Intersection.CurveBrepFace(ray, face, tol / 10, out var overlaps, out var hits)) return No("intersection failed");
        double first = double.PositiveInfinity; bool undecidable = false;
        foreach (var h in hits)
        {
            double s = (h - point) * e;
            if (s <= tol) continue;
            if (s <= graze)
            {
                if (!tangentCandidate) { undecidable = true; continue; }
                // A hit inside the grazing depth is the tangency only if the ray also grazes the surface there.
                if (face.ClosestPoint(h, out var hu, out var hv) && Math.Abs(face.NormalAt(hu, hv) * e) <= .05) continue;
            }
            first = Math.Min(first, s);
        }
        foreach (var o in overlaps)
        {
            double s0 = (o.PointAtStart - point) * e, s1 = (o.PointAtEnd - point) * e; if (s0 > s1) (s0, s1) = (s1, s0); o.Dispose();
            if (s1 <= tol) continue;
            if (s0 <= graze) { undecidable |= !tangentCandidate || s1 > graze; continue; }
            first = Math.Min(first, s0);
        }
        return undecidable ? No(tangentCandidate ? "silhouette, ray runs along the surface" : "edge, hit inside the grazing depth") : double.IsInfinity(first) ? (reach, false, ne) : (first - tol - 1e-6, true, ne);
    }
    /// <summary>Depth range of the own-face ray test. Beyond it nothing is exempt: the mesh decides.</summary>
    private const double LocalDepth = .25;
    private static bool Exact(FaceKind kind) => kind.Planar || kind.Cylinder is not null || kind.Sphere is not null;
    private readonly record struct FaceKind(bool Planar, Cylinder? Cylinder, Sphere? Sphere);
    private static FaceKind Kind(BrepFace face, double tol) =>
        face.TryGetPlane(out _, tol) ? new(true, null, null) : face.TryGetCylinder(out var c, tol) ? new(false, c, null) : face.TryGetSphere(out var s, tol) ? new(false, null, s) : new(false, null, null);

    /// <summary>Strict recognition of a whole body as an analytic solid; null when anything does not fit.</summary>
    /// <summary>Would the prototype take this body as an exact solid? Used by the census only.</summary>
    internal static string? RecognisedAs(Brep brep, double tol)
    {
        var solid = Recognise(brep, tol, Transform.Identity, p => new V3(p.X, p.Y, p.Z), d => new V3(d.X, d.Y, d.Z), new int[brep.Faces.Count], out _);
        return solid?.GetType().Name;
    }
    private sealed record Shape(Sphere? Sphere, Point3d Origin, Vector3d Axis, double Outer, double Inner, Plane[] Caps, int OuterFace, int InnerFace, bool Sheet = false);
    private static Solid? Recognise(Brep brep, double tol, Transform toView, Func<Point3d, V3> project, Func<Vector3d, V3> direction, int[] tags, out Shape? shape)
    {
        shape = null;
        if (!brep.IsSolid && brep.Faces.Count == 1 && brep.Faces[0].TryGetCylinder(out var sheet, tol))
        {
            // An extruded arc: one cylindrical face bounded by rulings and by arcs in planes across the axis.
            var u = sheet.Axis; if (!u.Unitize()) return null; var o = sheet.Center; var x = sheet.BasePlane.XAxis; var y = Vector3d.CrossProduct(u, x); if (!y.Unitize()) return null; x = Vector3d.CrossProduct(y, u);
            double h0 = double.PositiveInfinity, h1 = double.NegativeInfinity, phi = double.NaN, sweep = double.NaN; int rulings = 0, arcs = 0;
            foreach (var edge in brep.Edges)
            {
                if (edge.Valence == EdgeAdjacency.Interior) { if (!edge.IsLinear(tol)) return null; continue; }   // seam
                if (edge.IsLinear(tol))
                {
                    var d = edge.PointAtEnd - edge.PointAtStart; if (!d.Unitize() || d.IsParallelTo(u, 1e-6) == 0) return null; rulings++; continue;
                }
                if (!edge.TryGetArc(out var arc, tol) || Math.Abs(arc.Radius - sheet.Radius) > tol || arc.Plane.Normal.IsParallelTo(u, 1e-6) == 0) return null;
                double h = (arc.Center - o) * u; if (new Line(o, o + u).DistanceTo(arc.Center, false) > tol) return null;
                h0 = Math.Min(h0, h); h1 = Math.Max(h1, h); arcs++;
                double Angle(Point3d q) => Math.Atan2((q - o) * y, (q - o) * x);
                double a0 = Angle(edge.PointAtStart), am = Angle(edge.PointAt(edge.Domain.Mid)), a1 = Angle(edge.PointAtEnd);
                double Mod(double v) { v %= 2 * Math.PI; return v < 0 ? v + 2 * Math.PI : v; }
                double d1 = Mod(am - a0), d2 = edge.IsClosed ? 2 * Math.PI : Mod(a1 - a0);
                double start = d1 <= d2 ? a0 : a1, span = d1 <= d2 ? d2 : 2 * Math.PI - d2;
                if (double.IsNaN(phi)) { phi = start; sweep = span; }
                else if (Math.Abs(span - sweep) > 1e-6 || Math.Min(Mod(start - phi), Mod(phi - start)) > 1e-6) return null;
            }
            if (arcs != 2 || rulings is not (0 or 2) || h1 - h0 <= tol || double.IsNaN(phi)) return null;
            double area = sheet.Radius * sweep * (h1 - h0); if (Math.Abs(brep.GetArea() - area) > 1e-4 * area) return null;
            var bounds = brep.GetBoundingBox(toView); tags[0] = Solid.Outer;
            shape = new(null, o, u, sheet.Radius, 0, Array.Empty<Plane>(), 0, -1, true);
            return new CylinderPatch(project(o), direction(u), direction(x), direction(y), sheet.Radius, phi, sweep, h0, h1, new V3(bounds.Min.X, bounds.Min.Y, bounds.Min.Z), new V3(bounds.Max.X, bounds.Max.Y, bounds.Max.Z));
        }
        if (!brep.IsSolid || !brep.IsManifold) return null;
        var cylinders = new List<(int Face, Cylinder C)>(); var planes = new List<Plane>(); var spheres = new List<Sphere>();
        foreach (var face in brep.Faces)
        {
            if (face.TryGetPlane(out var plane, tol)) planes.Add(plane);
            else if (face.TryGetCylinder(out var cylinder, tol)) cylinders.Add((face.FaceIndex, cylinder));
            else if (face.TryGetSphere(out var sphere, tol)) spheres.Add(sphere);
            else return null;
        }
        var box = brep.GetBoundingBox(toView); double volume = brep.GetVolume();
        if (spheres.Count == brep.Faces.Count)
        {
            var s = spheres[0];
            if (spheres.Any(x => x.Center.DistanceTo(s.Center) > tol || Math.Abs(x.Radius - s.Radius) > tol)) return null;
            if (Math.Abs(volume - 4.0 / 3 * Math.PI * Math.Pow(s.Radius, 3)) > 1e-4 * volume) return null;
            for (int i = 0; i < tags.Length; i++) tags[i] = Solid.Outer;
            shape = new(s, s.Center, Vector3d.Zero, s.Radius, 0, Array.Empty<Plane>(), 0, -1);
            return new Ball(project(s.Center), s.Radius);
        }
        if (spheres.Count > 0 || cylinders.Count == 0 || planes.Count != 2) return null;
        var first = cylinders[0].C; var axis = first.Axis; if (!axis.Unitize()) return null; var origin = first.Center;
        double outer = cylinders.Max(c => c.C.Radius), inner = cylinders.Min(c => c.C.Radius);
        foreach (var (_, c) in cylinders)
        {
            if (c.Axis.IsParallelTo(axis, 1e-7) == 0 || new Line(origin, origin + axis).DistanceTo(c.Center, false) > tol) return null;
            if (Math.Abs(c.Radius - outer) > tol && Math.Abs(c.Radius - inner) > tol) return null;
        }
        if (outer - inner <= tol) inner = 0;
        // Each cap plane meets the axis; the two caps must not meet inside the outer cylinder.
        var cuts = new double[2]; var reach = new double[2];
        for (int i = 0; i < 2; i++)
        {
            var n = planes[i].Normal; double un = axis * n; if (Math.Abs(un) < 1e-6) return null;
            cuts[i] = ((planes[i].Origin - origin) * n) / un; reach[i] = outer * Math.Sqrt(Math.Max(0, 1 - un * un)) / Math.Abs(un);
        }
        if (Math.Abs(cuts[1] - cuts[0]) <= reach[0] + reach[1] + tol) return null;
        double expected = Math.PI * (outer * outer - inner * inner) * Math.Abs(cuts[1] - cuts[0]);
        if (Math.Abs(volume - expected) > 1e-4 * expected) return null;
        var middle = origin + axis * ((cuts[0] + cuts[1]) / 2); var halves = new (V3 N, double D)[2];
        for (int i = 0; i < 2; i++)
        {
            var n = planes[i].Normal; double d = (planes[i].Origin - Point3d.Origin) * n;
            if ((middle - Point3d.Origin) * n > d) { n = -n; d = -d; }
            // In view coordinates: N·x <= D with x measured from the view plane origin.
            var nv = direction(n); var po = project(planes[i].Origin); halves[i] = (nv, V3.Dot(nv, po));
        }
        foreach (var (face, c) in cylinders) tags[face] = Math.Abs(c.Radius - outer) <= tol ? Solid.Outer : Solid.Inner;
        shape = new(null, origin, axis, outer, inner, planes.ToArray(), cylinders.First(c => Math.Abs(c.C.Radius - outer) <= tol).Face, inner > 0 ? cylinders.First(c => Math.Abs(c.C.Radius - inner) <= tol).Face : -1);
        return new Tube(project(origin), direction(axis), outer, inner, halves, new[] { new V3(box.Min.X, box.Min.Y, box.Min.Z), new V3(box.Max.X, box.Max.Y, box.Max.Z) });
    }
}
