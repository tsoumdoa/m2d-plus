using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Environment = System.Environment;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// Curved spike (hillclimb/curved-spike). Harness only; no product code is changed.
///   census      product solve with observers, then per curved native input: is its whole view-frame bounding rectangle
///               at its nearest depth covered by the planar occluders (exact point predicate over a slab decomposition)?
///               Plus a sampled estimate and the ceiling (sources with no non-hidden output). Raw native HLD is timed on the
///               observed native inputs with and without each set (cost attribution only).
///   cull        product view solve on the full source list and with each set removed, back to back; dumps for compare.py.
///   mesh:k      curved Breps as meshes (chord tolerance k x model tolerance) through the exact planar kernel.
/// </summary>
internal static class Spike
{
    private sealed record NativeItem(GeometryBase Geometry, string Id, bool KernelEdge);
    private sealed class Capture
    {
        public PlanarExtraction? Extraction; public Plane Plane; public List<NativeItem> Native = new();
        public HashSet<string> KernelSources = new(StringComparer.Ordinal); public int NativeCalls;
    }

    private static ViewDrawing SolveView(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, double timeout)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ViewDrawing)solve.Invoke(null, new object[] { scene, sources, view, settings, limit.Token })!;
    }

    private static (ViewDrawing Drawing, Capture Capture, double Wall) Observe(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, double timeout)
    {
        var capture = new Capture();
        var settings = new NativeSettings
        {
            ObservePlanarScene = (extraction, plane) => { capture.Extraction ??= extraction; capture.Plane = plane; },
            ObserveKernelSources = ids => capture.KernelSources.UnionWith(ids),
            ObserveNativeInputs = inputs =>
            {
                if (capture.NativeCalls++ != 0) return;
                foreach (var input in inputs) { var copy = input.Geometry.Duplicate(); capture.Native.Add(new(copy, input.Source.Identity, input.KernelCandidate)); }
            }
        };
        var timer = Stopwatch.StartNew();
        var drawing = SolveView(scene, sources, view, settings, timeout);
        var wall = timer.Elapsed.TotalSeconds;
        if (capture.Extraction is null) capture.Plane = drawing.Frame.Plane;
        return (drawing, capture, wall);
    }

    private static string Phases(ViewDrawing d) => string.Join(", ", d.Phases.Seconds.Where(p => p.Value >= .005).Select(p => $"{p.Key}={p.Value:F2}"));
    private static double Phase(ViewDrawing d, string name) => d.Phases.Seconds.GetValueOrDefault(name);

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, ViewRequest view, string stem, string output, string[] experiments, double timeout)
    {
        var tol = scene.Tolerance; var rows = new List<object>();
        void Save() => File.WriteAllText(Path.Combine(output, stem + "-spike.json"), JsonSerializer.Serialize(new
        { view = view.Name, tolerance = tol, rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), rows }, new JsonSerializerOptions { WriteIndented = true }));

        // Baseline product solve with observers. Run twice: the first is cold (JIT, native caches).
        var warm = Observe(scene, prepared, view, timeout); warm.Drawing.Dispose(); foreach (var n in warm.Capture.Native) n.Geometry.Dispose();
        var busy0 = Busy.Start();
        var (drawing, capture, wall) = Observe(scene, prepared, view, timeout);
        var baseBusy = busy0.Stop();
        Console.WriteLine($"--- {stem} [base] wall {wall:F3}s (cold {warm.Wall:F3}s) native solve {Phase(drawing, "native solve"):F3}s; phases: {Phases(drawing)}; pieces {drawing.Items.Count}; error {drawing.Error}; busy {baseBusy}");
        Console.WriteLine($"    route: {drawing.Report.Route}");
        var plane = capture.Plane; var toXY = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var faces = (IReadOnlyList<PlanarFace>?)capture.Extraction?.Faces ?? Array.Empty<PlanarFace>();
        var curved = capture.Native.Where(n => !n.KernelEdge && n.Geometry is not (Curve or Point)).ToArray();
        var nonHidden = drawing.Items.Where(p => p.Class != LineClass.Hidden).Select(p => p.SourceIdentity).ToHashSet(StringComparer.Ordinal);
        var curvedSources = curved.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
        rows.Add(new { name = "base", wall, cold = warm.Wall, nativeSolve = Phase(drawing, "native solve"), phases = drawing.Phases.Seconds, pieces = drawing.Items.Count, drawing.Error,
            route = drawing.Report.Route, sources = prepared.Count, planarFaces = faces.Count, nativeInputs = capture.Native.Count,
            kernelEdgesInNative = capture.Native.Count(n => n.KernelEdge), curvedRegistrations = curved.Length, curvedSources = curvedSources.Length, busy = baseBusy });

        // ---- Census ----
        var proved = new HashSet<string>(StringComparer.Ordinal); var sampled = new HashSet<string>(StringComparer.Ordinal); var sampledAny = new HashSet<string>(StringComparer.Ordinal); var aligned = new HashSet<string>(StringComparer.Ordinal);
        var ceiling = curvedSources.Where(id => !nonHidden.Contains(id)).ToHashSet(StringComparer.Ordinal);
        {
            var timer = Stopwatch.StartNew(); long queries = 0; int complex = 0;
            EdgeFaceKernel.PointQuery? query = faces.Count == 0 ? null : new EdgeFaceKernel.PointQuery(faces, tol);
            var failedProof = new HashSet<string>(StringComparer.Ordinal); var failedSample = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in curved)
            {
                var box = item.Geometry.GetBoundingBox(toXY);
                bool p = false, s = false;
                if (query is not null && box.IsValid)
                {
                    double x0 = box.Min.X - tol, y0 = box.Min.Y - tol, x1 = box.Max.X + tol, y1 = box.Max.Y + tol, z = box.Max.Z;
                    s = true;
                    for (int i = 0; i <= 8 && s; i++) for (int j = 0; j <= 8 && s; j++)
                        s = query.Classify(new Vertex(x0 + (x1 - x0) * i / 8, y0 + (y1 - y0) * j / 8, z)) == VisibilityClass.Hidden;
                    if (s) { p = Covered(query, faces, x0, y0, x1, y1, z, tol, ref queries, out bool tooComplex); if (tooComplex) complex++; }
                }
                if (!p) failedProof.Add(item.Id); if (!s) failedSample.Add(item.Id);
            }
            foreach (var id in curvedSources) { if (!failedProof.Contains(id)) proved.Add(id); if (!failedSample.Contains(id)) sampled.Add(id); }
            // A source that also owns kernel candidates (generated planar pieces) is not removed as a whole.
            proved.ExceptWith(capture.KernelSources); sampled.ExceptWith(capture.KernelSources);
            // Aligned repeats: a closed solid that is another instance of the same definition moved straight along the view
            // direction, away from the viewer. Every view ray through it passes through the nearer copy first, so it is
            // wholly hidden. Tested on corresponding Brep vertices (same definition, so the same vertex order).
            if (view.Cut is null)
            {
                var keys = prepared.Where(l => l.Canonical is not null).GroupBy(l => l.Identity).ToDictionary(g => g.Key, g => g.First().Canonical!.Key + "/" + g.First().Canonical!.Scale.ToString("R"), StringComparer.Ordinal);
                Vertex ToView(Point3d q) { var d = q - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
                foreach (var group in curved.Where(c => c.Geometry is Brep b && b.IsSolid && b.Vertices.Count > 0 && keys.ContainsKey(c.Id)).GroupBy(c => keys[c.Id]))
                {
                    var members = group.Select(c => (c.Id, V: ((Brep)c.Geometry).Vertices.Select(v => ToView(v.Location)).ToArray())).ToArray();
                    foreach (var b in members)
                        foreach (var a in members)
                        {
                            if (ReferenceEquals(a.V, b.V) || a.V.Length != b.V.Length) continue;
                            double shift = a.V[0].Z - b.V[0].Z; if (shift <= 10 * tol) continue;
                            bool same = true;
                            for (int i = 0; i < a.V.Length && same; i++)
                                same = Math.Abs(a.V[i].X - b.V[i].X) <= tol / 10 && Math.Abs(a.V[i].Y - b.V[i].Y) <= tol / 10 && Math.Abs(a.V[i].Z - b.V[i].Z - shift) <= tol / 10;
                            if (same) { aligned.Add(b.Id); break; }
                        }
                }
                aligned.ExceptWith(capture.KernelSources);
                Console.WriteLine($"--- {stem} [census aligned repeats] {aligned.Count} of {curvedSources.Length} curved sources are a nearer instance moved along the view direction; with non-hidden output {aligned.Count(nonHidden.Contains)}; in ceiling {aligned.Count(ceiling.Contains)}");
                rows.Add(new { name = "census aligned repeats", aligned = aligned.Count, withNonHiddenOutput = aligned.Where(nonHidden.Contains).Take(20).ToArray(), inCeiling = aligned.Count(ceiling.Contains) });
            }
            // Estimate only: could a proof that also uses curved occluders (through conservative inner proxies) reach more?
            // Occluders here are the planar faces plus a render mesh of every curved source; 9 x 9 samples of each footprint
            // rectangle at its nearest depth. A source's own triangles are never in front of that depth.
            if (experiments.Contains("census"))
            {
                var anyFaces = new List<PlanarFace>(faces);
                Vertex Project(Point3d q) { var d = q - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
                foreach (var item in curved)
                {
                    using var brep = item.Geometry switch { Brep b => b.DuplicateBrep(), Extrusion e => e.ToBrep(), SubD sd => sd.ToBrep(), Surface sf => sf.ToBrep(), _ => null };
                    var meshes = brep is null ? (item.Geometry is Mesh m ? new[] { m.DuplicateMesh() } : null) : Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                    if (meshes is null) continue;
                    foreach (var mesh in meshes)
                    {
                        mesh.Faces.ConvertQuadsToTriangles();
                        foreach (var f in mesh.Faces) anyFaces.Add(new(new[] { new[] { Project(mesh.Vertices.Point3dAt(f.A)), Project(mesh.Vertices.Point3dAt(f.B)), Project(mesh.Vertices.Point3dAt(f.C)) } }));
                        mesh.Dispose();
                    }
                }
                var anyQuery = new EdgeFaceKernel.PointQuery(anyFaces, tol); var failedAny = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in curved)
                {
                    var box = item.Geometry.GetBoundingBox(toXY); bool hiddenEverywhere = box.IsValid;
                    double x0 = box.Min.X - tol, y0 = box.Min.Y - tol, x1 = box.Max.X + tol, y1 = box.Max.Y + tol, z = box.Max.Z;
                    for (int i = 0; i <= 8 && hiddenEverywhere; i++) for (int j = 0; j <= 8 && hiddenEverywhere; j++)
                        hiddenEverywhere = anyQuery.Classify(new Vertex(x0 + (x1 - x0) * i / 8, y0 + (y1 - y0) * j / 8, z)) == VisibilityClass.Hidden;
                    if (!hiddenEverywhere) failedAny.Add(item.Id);
                }
                foreach (var id in curvedSources) if (!failedAny.Contains(id) && !capture.KernelSources.Contains(id)) sampledAny.Add(id);
                Console.WriteLine($"--- {stem} [census any-occluder estimate] sampled 9x9 hidden behind planar + meshed curved occluders: {sampledAny.Count} of {curvedSources.Length} ({anyFaces.Count - faces.Count} triangles); of those with non-hidden output {sampledAny.Count(nonHidden.Contains)}; ceiling {ceiling.Count}; in ceiling {sampledAny.Count(ceiling.Contains)}");
                rows.Add(new { name = "census any-occluder estimate", sampledAny = sampledAny.Count, triangles = anyFaces.Count - faces.Count, withNonHiddenOutput = sampledAny.Count(nonHidden.Contains), inCeiling = sampledAny.Count(ceiling.Contains) });
            }
            var seconds = timer.Elapsed.TotalSeconds;
            Console.WriteLine($"--- {stem} [census] curved sources {curvedSources.Length} (registrations {curved.Length}); planar faces {faces.Count}; proved hidden behind planar {proved.Count}; sampled 9x9 hidden {sampled.Count}; ceiling (no non-hidden output) {ceiling.Count}; proof {seconds:F3}s, {queries} point queries, {complex} too complex; proved-but-has-nonhidden-output {proved.Count(nonHidden.Contains)}");
            rows.Add(new { name = "census", curvedSources = curvedSources.Length, curvedRegistrations = curved.Length, planarFaces = faces.Count, proved = proved.Count, sampled = sampled.Count, ceiling = ceiling.Count,
                proofSeconds = seconds, queries, complex, provedWithNonHiddenOutput = proved.Where(nonHidden.Contains).ToArray() });
        }
        Save();

        foreach (var experiment in experiments)
        {
            if (experiment == "census")
            {
                // Raw native attribution on the observed native inputs.
                var full = Hld(capture.Native, drawing.Frame, view, scene, tol, timeout);
                Console.WriteLine($"--- {stem} [raw native full] {full.Seconds:F3}s inputs {full.Inputs} {full.Failure}");
                rows.Add(new { name = "raw native full", full });
                foreach (var (label, set) in new[] { ("proved", proved), ("sampled", sampled), ("aligned", aligned), ("any-occluder", sampledAny), ("ceiling", ceiling) })
                {
                    if (set.Count == 0) continue;
                    if (label == "sampled" && set.SetEquals(proved)) continue;
                    if (label == "any-occluder" && set.SetEquals(sampled)) continue;
                    var reduced = Hld(capture.Native.Where(n => n.KernelEdge || !set.Contains(n.Id)).ToArray(), drawing.Frame, view, scene, tol, timeout);
                    Console.WriteLine($"--- {stem} [raw native without {label} x{set.Count}] {reduced.Seconds:F3}s inputs {reduced.Inputs} ({(1 - reduced.Seconds / full.Seconds):P1} saved) {reduced.Failure}");
                    rows.Add(new { name = "raw native without " + label, removedSources = set.Count, reduced, savedFraction = 1 - reduced.Seconds / full.Seconds });
                }
                var again = Hld(capture.Native, drawing.Frame, view, scene, tol, timeout);
                Console.WriteLine($"--- {stem} [raw native full repeat] {again.Seconds:F3}s");
                rows.Add(new { name = "raw native full repeat", again });
            }
            else if (experiment == "cull")
            {
                Dump(drawing, plane, tol, Path.Combine(output, stem + "-base"), null);
                foreach (var (label, set) in new[] { ("proved", proved), ("aligned", aligned), ("any-occluder", sampledAny), ("ceiling", ceiling) })
                {
                    if (set.Count == 0) { Console.WriteLine($"--- {stem} [cull {label}] empty set"); continue; }
                    if (label == "any-occluder" && set.SetEquals(proved)) continue;
                    var kept = prepared.Where(p => !set.Contains(p.Identity)).ToArray();
                    var busy = Busy.Start(); var timer = Stopwatch.StartNew();
                    using var culled = SolveView(scene, kept, view, new NativeSettings(), timeout);
                    var seconds = timer.Elapsed.TotalSeconds; var load = busy.Stop();
                    busy = Busy.Start(); timer.Restart();
                    using var again = SolveView(scene, prepared, view, new NativeSettings(), timeout);
                    var baseSeconds = timer.Elapsed.TotalSeconds; var baseLoad = busy.Stop();
                    Console.WriteLine($"--- {stem} [cull {label} x{set.Count}] {seconds:F3}s native {Phase(culled, "native solve"):F3}s | base back-to-back {baseSeconds:F3}s native {Phase(again, "native solve"):F3}s | saved {(1 - seconds / baseSeconds):P1}; busy {load} / {baseLoad}; error {culled.Error}");
                    Dump(culled, plane, tol, Path.Combine(output, stem + "-cull-" + label), null);
                    Dump(again, plane, tol, Path.Combine(output, stem + "-base-vs-" + label), set);
                    rows.Add(new { name = "cull " + label, removedSources = set.Count, seconds, nativeSolve = Phase(culled, "native solve"), baseSeconds, baseNativeSolve = Phase(again, "native solve"),
                        savedFraction = 1 - seconds / baseSeconds, busy = load, baseBusy = baseLoad, pieces = culled.Items.Count, basePieces = again.Items.Count, culled.Error });
                }
            }
            else if (experiment.StartsWith("meshparams:", StringComparison.Ordinal))
                MeshKernel.Parameters(capture.Native.Select(n => (n.Geometry, n.Id, n.KernelEdge)).ToArray(), tol * double.Parse(experiment[11..], System.Globalization.CultureInfo.InvariantCulture));
            else if (experiment.StartsWith("mesh:", StringComparison.Ordinal))
            {
                Dump(drawing, plane, tol, Path.Combine(output, stem + "-base"), null);
                var unsupported = MeshKernel.Run(capture.Extraction, capture.Native.Select(n => (n.Geometry, n.Id, n.KernelEdge)).ToArray(), plane, view, tol,
                    double.Parse(experiment[5..], System.Globalization.CultureInfo.InvariantCulture), stem, output, rows, wall, Phase(drawing, "native solve"));
                Dump(drawing, plane, tol, Path.Combine(output, stem + "-ref"), unsupported);
            }
            else throw new ArgumentException("Unknown spike experiment " + experiment);
            Save();
        }
        drawing.Dispose(); foreach (var n in capture.Native) n.Geometry.Dispose();
    }

    /// <summary>
    /// Is the rectangle [x0,x1] x [y0,y1] at depth z hidden everywhere by the planar faces? Slab decomposition: within a slab
    /// between consecutive event ordinates no two boundary or depth-crossing lines meet, so the cover is constant on each cell
    /// between consecutive lines; one exact point decision per cell decides it. The union of closed polygons is closed, so
    /// covering every open cell covers the rectangle. Events are ordered in doubles here; a production version must order
    /// them in rational arithmetic to be a proof.
    /// </summary>
    private static bool Covered(EdgeFaceKernel.PointQuery query, IReadOnlyList<PlanarFace> faces, double x0, double y0, double x1, double y1, double z, double tol, ref long queries, out bool tooComplex)
    {
        tooComplex = false;
        var segments = new List<(double Ax, double Ay, double Bx, double By)>();
        foreach (int id in query.OverlappingFaces(x0, y0, x1, y1))
        {
            var all = faces[id].Loops.SelectMany(l => l).ToArray();
            if (all.Max(p => p.Z) <= z + tol) continue; // can never be more than a tolerance in front
            foreach (var loop in faces[id].Loops)
                for (int i = 0; i < loop.Length; i++)
                {
                    var a = loop[i]; var b = loop[(i + 1) % loop.Length];
                    if (Math.Max(a.X, b.X) < x0 || Math.Min(a.X, b.X) > x1 || Math.Max(a.Y, b.Y) < y0 || Math.Min(a.Y, b.Y) > y1) continue;
                    segments.Add((a.X, a.Y, b.X, b.Y));
                }
            if (all.Min(p => p.Z) > z + tol) continue;
            // The face passes through depth z + tol: add the line where its depth gap changes sign.
            var o = all[0]; double nx = 0, ny = 0, nz = 0;
            for (int i = 1; i + 1 < all.Length && nx == 0 && ny == 0 && nz == 0; i++)
            {
                double ux = all[i].X - o.X, uy = all[i].Y - o.Y, uz = all[i].Z - o.Z, vx = all[i + 1].X - o.X, vy = all[i + 1].Y - o.Y, vz = all[i + 1].Z - o.Z;
                nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx;
            }
            double planar = nx * nx + ny * ny;
            if (nz == 0 || planar == 0) continue;
            double k = nz * (o.Z - z - tol) / planar; double px = o.X + nx * k, py = o.Y + ny * k;
            double length = Math.Sqrt(planar); double dx = -ny / length, dy = nx / length;
            double reach = Math.Abs(x1 - x0) + Math.Abs(y1 - y0) + Math.Abs(px - x0) + Math.Abs(py - y0);
            segments.Add((px - dx * reach, py - dy * reach, px + dx * reach, py + dy * reach));
        }
        if (segments.Count > Limit("PERFF_PROOF_SEGMENTS", 400)) { tooComplex = true; return false; }
        var ys = new List<double> { y0, y1 };
        foreach (var s in segments) { if (s.Ay > y0 && s.Ay < y1) ys.Add(s.Ay); if (s.By > y0 && s.By < y1) ys.Add(s.By); }
        for (int i = 0; i < segments.Count; i++)
            for (int j = i + 1; j < segments.Count; j++)
            {
                var a = segments[i]; var b = segments[j];
                double rx = a.Bx - a.Ax, ry = a.By - a.Ay, sx = b.Bx - b.Ax, sy = b.By - b.Ay, den = rx * sy - ry * sx;
                if (den == 0) continue;
                double t = ((b.Ax - a.Ax) * sy - (b.Ay - a.Ay) * sx) / den, u = ((b.Ax - a.Ax) * ry - (b.Ay - a.Ay) * rx) / den;
                if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                double y = a.Ay + ry * t; if (y > y0 && y < y1) ys.Add(y);
            }
        ys.Sort(); var xs = new List<double>(); long local = 0;
        for (int i = 1; i < ys.Count; i++)
        {
            if (ys[i] == ys[i - 1]) continue;
            double ym = (ys[i] + ys[i - 1]) / 2; xs.Clear(); xs.Add(x0); xs.Add(x1);
            foreach (var s in segments)
            {
                if ((s.Ay > ym) == (s.By > ym)) continue;
                double x = s.Ax + (ym - s.Ay) / (s.By - s.Ay) * (s.Bx - s.Ax);
                if (x > x0 && x < x1) xs.Add(x);
            }
            xs.Sort();
            for (int j = 1; j < xs.Count; j++)
            {
                if (xs[j] == xs[j - 1]) continue;
                if (++local > Limit("PERFF_PROOF_QUERIES", 50000)) { tooComplex = true; queries += local; return false; }
                if (query.Classify(new Vertex((xs[j] + xs[j - 1]) / 2, ym, z)) != VisibilityClass.Hidden) { queries += local; return false; }
            }
        }
        queries += local; return true;
    }

    private static int Limit(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    internal sealed record Solve(double Seconds, int Inputs, int Visible, int Hidden, int Unset, string? Failure);

    private static Solve Hld(IReadOnlyList<NativeItem> items, Rectangle3d frame, ViewRequest view, PreparedScene scene, double tolerance, double timeout)
    {
        if (items.Count == 0) return new(0, 0, 0, 0, 0, "empty");
        using var viewport = new ViewportInfo(); var radius = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Diagonal.Length, 1) : 1;
        viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(frame.Plane.Origin + frame.Plane.ZAxis * (radius * 4 + 10));
        viewport.SetCameraDirection(view.Direction); viewport.SetCameraUp(frame.Plane.YAxis);
        viewport.SetFrustum(frame.X.Min, frame.X.Max, frame.Y.Min, frame.Y.Max, .001, radius * 10 + 100);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tolerance, Flatten = false, IncludeHiddenCurves = true, IncludeTangentEdges = false, IncludeTangentSeams = false };
        parameters.SetViewport(viewport);
        foreach (var item in items) if (!parameters.AddGeometry(item.Geometry, item.Id)) return new(0, items.Count, 0, 0, 0, "native rejected " + item.Id);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var timer = Stopwatch.StartNew(); HiddenLineDrawing? drawing;
        try { drawing = HiddenLineDrawing.Compute(parameters, false, null, limit.Token); }
        catch (OperationCanceledException) { return new(timer.Elapsed.TotalSeconds, items.Count, 0, 0, 0, "timeout"); }
        var seconds = timer.Elapsed.TotalSeconds;
        if (drawing is null) return new(seconds, items.Count, 0, 0, 0, "null drawing");
        using (drawing)
        {
            int visible = 0, hidden = 0, unset = 0;
            foreach (var segment in drawing.Segments)
            {
                var v = segment.SegmentVisibility;
                if (v == HiddenLineDrawingSegment.Visibility.Visible) visible++; else if (v == HiddenLineDrawingSegment.Visibility.Hidden) hidden++; else if (v == HiddenLineDrawingSegment.Visibility.Unset) unset++;
            }
            return new(seconds, items.Count, visible, hidden, unset, null);
        }
    }

    /// <summary>2D dumps in the curved raw-HLD format compare.py reads: all classes, non-hidden only, and hidden only (optionally without some sources).</summary>
    internal static void Dump(ViewDrawing drawing, Plane plane, double tolerance, string stem, ISet<string>? withoutSources)
    {
        var all = new List<object>(); var visible = new List<object>(); var hidden = new List<object>();
        foreach (var item in drawing.Items)
        {
            using var geometry = item.CopyGeometry();
            double[] points;
            if (geometry is Point point) { plane.ClosestParameter(point.Location, out var u, out var v); points = new[] { u, v }; }
            else if (geometry is Curve curve)
            {
                Polyline? polyline = null;
                if (!curve.TryGetPolyline(out polyline))
                {
                    using var approximation = curve.ToPolyline(tolerance / 8, .02, 0, 0);
                    if (approximation is null || !approximation.TryGetPolyline(out polyline)) polyline = new Polyline(new[] { curve.PointAtStart, curve.PointAtEnd });
                }
                points = polyline.SelectMany(p => { plane.ClosestParameter(p, out var u, out var v); return new[] { u, v }; }).ToArray();
            }
            else continue;
            var piece = new { c = item.Class.ToString(), s = item.SourceIdentity, p = points };
            if (withoutSources is not null && withoutSources.Contains(item.SourceIdentity)) continue;
            all.Add(piece); if (item.Class == LineClass.Hidden) hidden.Add(piece); else visible.Add(piece);
        }
        File.WriteAllText(stem + "-all.json", JsonSerializer.Serialize(new { tolerance, planar2d = true, pieces = all }));
        File.WriteAllText(stem + "-visible.json", JsonSerializer.Serialize(new { tolerance, planar2d = true, pieces = visible }));
        File.WriteAllText(stem + "-hidden.json", JsonSerializer.Serialize(new { tolerance, planar2d = true, pieces = hidden }));
    }

    internal sealed class Busy
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        private long _idle, _kernel, _user; private TimeSpan _cpu; private readonly Stopwatch _timer = Stopwatch.StartNew();
        internal static Busy Start() { var b = new Busy(); GetSystemTimes(out b._idle, out b._kernel, out b._user); using var p = Process.GetCurrentProcess(); b._cpu = p.TotalProcessorTime; return b; }
        internal string Stop()
        {
            GetSystemTimes(out var idle, out var kernel, out var user); using var p = Process.GetCurrentProcess();
            var busy = 1 - (idle - _idle) / (double)Math.Max(1, kernel - _kernel + user - _user);
            var own = (p.TotalProcessorTime - _cpu).TotalSeconds / Math.Max(_timer.Elapsed.TotalSeconds, 1e-9);
            return $"machine {busy:P0}, foreign cores ~{Math.Max(0, busy * Environment.ProcessorCount - own):F1}";
        }
    }
}
