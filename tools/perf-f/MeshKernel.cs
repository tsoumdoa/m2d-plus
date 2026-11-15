using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Environment = System.Environment;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// Curved spike, question 2: curved Breps as fine meshes through the exact planar kernel.
/// Occluders are the mesh triangles. Candidates are (1) mesh edges lying on non-smooth Brep edges, (2) mesh edges where the
/// two triangles face opposite ways in the view (mesh silhouette), (3) polylines of free curves, plus the planar extraction.
/// Candidates are mesh edges themselves, so they share exact vertices with the occluders and the kernel's tie rule applies.
/// "poly" output is the mesh polylines. "smooth" output maps the intervals back: Brep-edge candidates to parameter intervals
/// of the Brep edge, mesh-silhouette intervals to Rhino's exact silhouette curves by proximity in the drawing.
/// </summary>
internal static class MeshKernel
{
    private readonly record struct Meta(int Kind, string Source, int Item, int BrepEdge, double Ta, double Tb); // Kind 0 planar, 1 Brep edge, 2 silhouette, 3 curve

    /// <summary>Which meshing parameters actually hold a chordal tolerance? Deviation is measured at triangle centroids and edge midpoints.</summary>
    internal static void Parameters((GeometryBase Geometry, string Id, bool KernelEdge)[] native, double eps)
    {
        MeshingParameters Bare(double t, int minCount, double minEdge, double gridAngle, double refineAngle) => new MeshingParameters { Tolerance = t, RelativeTolerance = 0, MinimumEdgeLength = minEdge, MaximumEdgeLength = 0,
            GridAngle = RhinoMath.ToRadians(gridAngle), GridAspectRatio = 6, GridAmplification = 1, GridMinCount = minCount, GridMaxCount = 0, RefineAngle = RhinoMath.ToRadians(refineAngle), RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true };
        var sets = new (string Name, Func<MeshingParameters> Make)[]
        {
            ("quality + tol + minEdge eps/10 + minCount 16", () => { var m = MeshingParameters.QualityRenderMesh; m.Tolerance = eps; m.MinimumEdgeLength = eps / 10; m.GridMinCount = 16; m.SimplePlanes = true; m.JaggedSeams = false; m.DoublePrecision = true; return m; }),
            ("bare minCount16 minEdge eps/10 grid20", () => Bare(eps, 16, eps / 10, 20, 0)),
            ("bare minCount16 minEdge eps/10 grid20, tol eps/2", () => Bare(eps / 2, 16, eps / 10, 20, 0)),
            ("bare minCount16 minEdge eps/10 grid20 refine20", () => Bare(eps, 16, eps / 10, 20, 20)),
            ("bare minCount64 minEdge eps/10 grid10", () => Bare(eps, 64, eps / 10, 10, 0)),
            ("bare minCount16 minEdge 0 grid20", () => Bare(eps, 16, 0, 20, 0)),
        };
        var breps = native.Where(n => !n.KernelEdge).Select(n => n.Geometry switch { Brep b => b, Extrusion e => e.ToBrep(), SubD s => s.ToBrep(), _ => null }).Where(b => b is not null).Take(60).ToArray();
        foreach (var (name, make) in sets)
        {
            var timer = Stopwatch.StartNew(); long triangles = 0; double worst = 0; int over = 0, samples = 0; var random = new Random(1);
            double seconds = 0;
            foreach (var brep in breps)
            {
                timer.Restart(); var meshes = Mesh.CreateFromBrep(brep!, make()); seconds += timer.Elapsed.TotalSeconds;
                if (meshes is null) continue;
                foreach (var mesh in meshes)
                {
                    mesh.Faces.ConvertQuadsToTriangles(); triangles += mesh.Faces.Count;
                    for (int s = 0; s < Math.Min(60, mesh.Faces.Count); s++)
                    {
                        var f = mesh.Faces[random.Next(mesh.Faces.Count)];
                        Point3d a = mesh.Vertices.Point3dAt(f.A), b = mesh.Vertices.Point3dAt(f.B), c = mesh.Vertices.Point3dAt(f.C);
                        foreach (var p in new[] { (a + b + c) / 3, (a + b) / 2, (b + c) / 2, (a + c) / 2 })
                        { double d = brep!.ClosestPoint(p).DistanceTo(p); worst = Math.Max(worst, d); samples++; if (d > eps) over++; }
                    }
                }
            }
            Console.WriteLine($"    meshparams eps {eps:G4} [{name}]: {seconds:F3}s, {triangles} triangles, worst deviation {worst:G4} ({worst / eps:F2} eps), {over}/{samples} samples over eps");
        }
    }

    internal static HashSet<string> Run(PlanarExtraction? extraction, (GeometryBase Geometry, string Id, bool KernelEdge)[] native, Plane plane, ViewRequest view,
        double tol, double multiple, string stem, string output, List<object> rows, double baseWall, double baseNative)
    {
        double eps = tol * multiple; var angle = RhinoMath.DefaultAngleTolerance;
        var faces = new List<PlanarFace>(); var edges = new List<CandidateEdge>(); var meta = new List<Meta>();
        if (extraction is not null)
        {
            faces.AddRange(extraction.Faces);
            foreach (var e in extraction.Edges) { edges.Add(e); meta.Add(new(0, e.Source, -1, -1, 0, 1)); }
        }
        int planarFaces = faces.Count;
        Vertex Project(Point3d p) { var d = p - plane.Origin; return new(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis); }
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var items = native.Where(n => !n.KernelEdge).ToArray();
        var breps = new Brep?[items.Length]; var curves = new Curve?[items.Length];
        double meshSeconds = 0, topologySeconds = 0, mapSeconds = 0; long triangles = 0; int nakedMesh = 0; double worstChord = 0; int meshedSources = 0, chordSamples = 0, chordOver = 0, kinkSplit = 0;
        // Rhino's mesher does not hold Tolerance by itself (meshparams experiment); half the target plus a minimum grid is the
        // closest tested setting. The achieved deviation is measured and reported, never assumed.
        var mp = new MeshingParameters { Tolerance = eps / 2, RelativeTolerance = 0, MinimumEdgeLength = 0, MaximumEdgeLength = 0, GridAngle = RhinoMath.ToRadians(20), GridAspectRatio = 6,
            GridAmplification = 1, GridMinCount = 16, GridMaxCount = 0, RefineAngle = 0, RefineGrid = true, SimplePlanes = true, JaggedSeams = false, DoublePrecision = true };
        var random = new Random(1);
        for (int index = 0; index < items.Length; index++)
        {
            var (geometry, id, _) = items[index];
            if (geometry is Curve curve)
            {
                curves[index] = curve;
                using var approximation = curve.ToPolyline(Math.Min(eps, tol) / 8, .05, 0, 0);
                if (approximation is null || !approximation.TryGetPolyline(out var polyline)) { unsupported.Add(id); continue; }
                for (int i = 1; i < polyline.Count; i++)
                {
                    curve.ClosestPoint(polyline[i - 1], out var ta); curve.ClosestPoint(polyline[i], out var tb);
                    if (i == 1) ta = curve.Domain.Min; if (i == polyline.Count - 1) tb = curve.Domain.Max;
                    edges.Add(new(Project(polyline[i - 1]), Project(polyline[i]), id)); meta.Add(new(3, id, index, -1, ta, tb));
                }
                continue;
            }
            Brep? brep = geometry switch { Brep b => b, Extrusion e => e.ToBrep(), SubD s => s.ToBrep(), Surface s => s.ToBrep(), _ => null };
            if (brep is null) { unsupported.Add(id); continue; }
            // A face with an internal kink draws a crease there in native. Splitting at kinks (same point set) turns
            // those creases into Brep edges, which the candidate rules below already handle. PERFF_NO_KINK=1 disables it.
            if (Environment.GetEnvironmentVariable("PERFF_NO_KINK") != "1")
            {
                var split = brep.DuplicateBrep();
                if (split.Faces.SplitKinkyFaces(angle, true) && split.IsValid && split.Faces.Count != brep.Faces.Count) { kinkSplit++; if (!ReferenceEquals(brep, geometry)) brep.Dispose(); brep = split; }
                else split.Dispose();
            }
            breps[index] = brep;
            var timer = Stopwatch.StartNew();
            var meshes = Mesh.CreateFromBrep(brep, mp);
            meshSeconds += timer.Elapsed.TotalSeconds; timer.Restart();
            if (meshes is null || meshes.Length != brep.Faces.Count) { unsupported.Add(id); continue; }
            meshedSources++;
            // Weld by exact coordinates; triangles carry their Brep face.
            var lookup = new Dictionary<Point3d, int>(); var world = new List<Point3d>(); var tris = new List<(int A, int B, int C, int Face)>();
            for (int f = 0; f < meshes.Length; f++)
            {
                var mesh = meshes[f]; mesh.Faces.ConvertQuadsToTriangles();
                var map = new int[mesh.Vertices.Count];
                for (int v = 0; v < map.Length; v++)
                {
                    var p = mesh.Vertices.Point3dAt(v);
                    if (!lookup.TryGetValue(p, out var w)) { w = world.Count; world.Add(p); lookup.Add(p, w); }
                    map[v] = w;
                }
                foreach (var face in mesh.Faces)
                {
                    int a = map[face.A], b = map[face.B], c = map[face.C];
                    if (a == b || b == c || a == c) continue;
                    tris.Add((a, b, c, f));
                }
                mesh.Dispose();
            }
            triangles += tris.Count;
            var projected = world.Select(Project).ToArray();
            foreach (var t in tris) faces.Add(new(new[] { new[] { projected[t.A], projected[t.B], projected[t.C] } }));
            // Measured chord deviation on a sample of triangle centroids.
            for (int s = 0; s < Math.Min(40, tris.Count); s++)
            {
                var t = tris[random.Next(tris.Count)]; var centre = (world[t.A] + world[t.B] + world[t.C]) / 3;
                double deviation = brep.ClosestPoint(centre).DistanceTo(centre); chordSamples++; if (deviation > eps) chordOver++;
                worstChord = Math.Max(worstChord, deviation);
            }
            // Brep face pairs: smooth (tangent) pairs are treated as one surface; others are creases.
            var pairEdges = new Dictionary<(int, int), List<int>>(); var nakedEdges = new Dictionary<int, List<int>>(); var smooth = new Dictionary<(int, int), bool>();
            foreach (var e in brep.Edges)
            {
                var adjacent = e.AdjacentFaces();
                if (adjacent.Length == 2 && adjacent[0] != adjacent[1])
                {
                    var key = (Math.Min(adjacent[0], adjacent[1]), Math.Max(adjacent[0], adjacent[1]));
                    if (!pairEdges.TryGetValue(key, out var list)) pairEdges.Add(key, list = new());
                    list.Add(e.EdgeIndex);
                    bool isSmooth = e.IsSmoothManifoldEdge(angle);
                    smooth[key] = smooth.TryGetValue(key, out var old) ? old && isSmooth : isSmooth;
                }
                else if (e.Valence == EdgeAdjacency.Naked && adjacent.Length == 1)
                {
                    if (!nakedEdges.TryGetValue(adjacent[0], out var list)) nakedEdges.Add(adjacent[0], list = new());
                    list.Add(e.EdgeIndex);
                }
            }
            var edgeMap = new Dictionary<(int, int), (int T1, int T2, int Count)>(tris.Count * 2);
            for (int t = 0; t < tris.Count; t++)
            {
                var tri = tris[t];
                foreach (var (a, b) in new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) })
                {
                    var key = a < b ? (a, b) : (b, a);
                    edgeMap[key] = edgeMap.TryGetValue(key, out var old) ? (old.T1, t, old.Count + 1) : (t, -1, 1);
                }
            }
            topologySeconds += timer.Elapsed.TotalSeconds; timer.Restart();
            int Third((int A, int B, int C, int Face) t, int a, int b) => t.A != a && t.A != b ? t.A : t.B != a && t.B != b ? t.B : t.C;
            double Side(Vertex a, Vertex b, Vertex c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            foreach (var (key, value) in edgeMap)
            {
                var (a, b) = key; int kind; List<int>? candidates = null;
                var t1 = tris[value.T1];
                if (value.Count == 1) { kind = 1; nakedEdges.TryGetValue(t1.Face, out candidates); if (candidates is null) nakedMesh++; }
                else if (value.Count == 2)
                {
                    var t2 = tris[value.T2];
                    var pair = (Math.Min(t1.Face, t2.Face), Math.Max(t1.Face, t2.Face));
                    if (t1.Face != t2.Face && !(smooth.TryGetValue(pair, out var isSmooth) && isSmooth)) { kind = 1; pairEdges.TryGetValue(pair, out candidates); }
                    else
                    {
                        double s1 = Side(projected[a], projected[b], projected[Third(t1, a, b)]), s2 = Side(projected[a], projected[b], projected[Third(t2, a, b)]);
                        if (s1 == 0 && s2 == 0) continue;
                        if (s1 * s2 < 0) continue; // the two triangles lie on opposite sides in the drawing: an interior, non-silhouette edge
                        kind = 2;
                    }
                }
                else kind = 1;
                int brepEdge = -1; double ta = 0, tb = 1;
                if (kind == 1 && candidates is not null)
                {
                    var mid = (world[a] + world[b]) / 2; double best = double.PositiveInfinity;
                    foreach (int ei in candidates)
                    {
                        var e = brep.Edges[ei];
                        if (e.ClosestPoint(mid, out var t) && e.PointAt(t).DistanceTo(mid) < best) { best = e.PointAt(t).DistanceTo(mid); brepEdge = ei; }
                    }
                    if (brepEdge >= 0)
                    {
                        var e = brep.Edges[brepEdge]; e.ClosestPoint(world[a], out ta); e.ClosestPoint(world[b], out tb);
                        var domain = e.Domain;
                        if (e.IsClosed && Math.Abs(tb - ta) > domain.Length / 2)
                        { if (ta < tb) ta = domain.Max; else tb = domain.Max; if (Math.Abs(tb - ta) > domain.Length / 2) { if (ta > tb) ta = domain.Min; else tb = domain.Min; } }
                    }
                }
                edges.Add(new(projected[a], projected[b], id)); meta.Add(new(kind, id, index, brepEdge, ta, tb));
            }
            mapSeconds += timer.Elapsed.TotalSeconds;
        }

        // Face preparation (exact plane validation per face, index build) is timed alone by a solve with no candidates.
        var solveTimer = Stopwatch.StartNew();
        EdgeFaceKernel.Solve(faces, Array.Empty<CandidateEdge>(), new(tol, Math.Min(Environment.ProcessorCount, 8)));
        var prepareSeconds = solveTimer.Elapsed.TotalSeconds; solveTimer.Restart();
        var result = EdgeFaceKernel.Solve(faces, edges, new(tol, Math.Min(Environment.ProcessorCount, 8)));
        var solveSeconds = solveTimer.Elapsed.TotalSeconds;
        string Class(VisibilityClass c) => c == VisibilityClass.Visible ? "Visible" : c == VisibilityClass.Hidden ? "Hidden" : "Unresolved";

        // ---- "poly" output: mesh polylines as they are ----
        var poly = new List<object>(); var lengths = new Dictionary<string, double>();
        foreach (var interval in result.Intervals)
        {
            var e = edges[interval.Edge]; var m = meta[interval.Edge];
            double ax = e.A.X + (e.B.X - e.A.X) * interval.Start, ay = e.A.Y + (e.B.Y - e.A.Y) * interval.Start, bx = e.A.X + (e.B.X - e.A.X) * interval.End, by = e.A.Y + (e.B.Y - e.A.Y) * interval.End;
            poly.Add(new { c = Class(interval.Class), s = m.Source, p = new[] { ax, ay, bx, by } });
            var key = $"kind{m.Kind}/{Class(interval.Class)}"; lengths[key] = lengths.GetValueOrDefault(key) + Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }
        var name = $"{stem}-mesh{multiple:G}";
        File.WriteAllText(Path.Combine(output, name + "-poly-all.json"), JsonSerializer.Serialize(new { tolerance = tol, planar2d = true, pieces = poly }));

        // ---- "smooth" output ----
        var smoothTimer = Stopwatch.StartNew(); var pieces = new List<object>(); double silhouetteSeconds = 0, unmatched = 0, matched = 0;
        double[] Flat(Curve c)
        {
            Polyline? polyline;
            if (!c.TryGetPolyline(out polyline))
            {
                using var approximation = c.ToPolyline(tol / 8, .02, 0, 0);
                if (approximation is null || !approximation.TryGetPolyline(out polyline)) polyline = new Polyline(new[] { c.PointAtStart, c.PointAtEnd });
            }
            return polyline.SelectMany(p => { var v = Project(p); return new[] { v.X, v.Y }; }).ToArray();
        }
        // Parameter intervals per exact curve (Brep edge or free curve).
        var perCurve = new Dictionary<(int Item, int Edge), List<(double Lo, double Hi, VisibilityClass Class)>>();
        var silhouetteSegments = new Dictionary<int, List<(double Ax, double Ay, double Bx, double By, VisibilityClass Class)>>();
        foreach (var interval in result.Intervals)
        {
            var e = edges[interval.Edge]; var m = meta[interval.Edge];
            double ax = e.A.X + (e.B.X - e.A.X) * interval.Start, ay = e.A.Y + (e.B.Y - e.A.Y) * interval.Start, bx = e.A.X + (e.B.X - e.A.X) * interval.End, by = e.A.Y + (e.B.Y - e.A.Y) * interval.End;
            if (m.Kind == 0 || m.Kind == 1 && m.BrepEdge < 0) { pieces.Add(new { c = Class(interval.Class), s = m.Source, p = new[] { ax, ay, bx, by } }); continue; }
            if (m.Kind == 2)
            {
                if (!silhouetteSegments.TryGetValue(m.Item, out var list)) silhouetteSegments.Add(m.Item, list = new());
                list.Add((ax, ay, bx, by, interval.Class)); continue;
            }
            double lo = m.Ta + (m.Tb - m.Ta) * interval.Start, hi = m.Ta + (m.Tb - m.Ta) * interval.End;
            if (lo > hi) (lo, hi) = (hi, lo);
            var key = (m.Item, m.Kind == 3 ? -1 : m.BrepEdge);
            if (!perCurve.TryGetValue(key, out var spans)) perCurve.Add(key, spans = new());
            spans.Add((lo, hi, interval.Class));
        }
        foreach (var (key, spans) in perCurve)
        {
            Curve curve = key.Edge < 0 ? curves[key.Item]! : breps[key.Item]!.Edges[key.Edge];
            spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            double slack = curve.Domain.Length * 1e-9; double start = spans[0].Lo, end = spans[0].Hi; var kind = spans[0].Class;
            void Emit(double a, double b, VisibilityClass c)
            {
                a = Math.Max(a, curve.Domain.Min); b = Math.Min(b, curve.Domain.Max);
                if (b - a <= slack) return;
                using var part = curve.Trim(a, b);
                if (part is not null) pieces.Add(new { c = Class(c), s = items[key.Item].Id, p = Flat(part) });
            }
            for (int i = 1; i < spans.Count; i++)
            {
                var s = spans[i];
                if (s.Class == kind && s.Lo <= end + slack) { end = Math.Max(end, s.Hi); continue; }
                Emit(start, end, kind); start = Math.Max(s.Lo, end); end = Math.Max(s.Hi, start); kind = s.Class;
            }
            Emit(start, end, kind);
        }
        // Exact silhouette curves, classified by the nearest mesh-silhouette interval of the same source in the drawing.
        double reach = Math.Max(4 * eps, 8 * tol);
        for (int index = 0; index < items.Length; index++)
        {
            if (breps[index] is not { } brep || unsupported.Contains(items[index].Id)) continue;
            var timer = Stopwatch.StartNew();
            var silhouettes = Silhouette.Compute(brep, SilhouetteType.Tangent, view.Direction, tol, angle);
            silhouetteSeconds += timer.Elapsed.TotalSeconds;
            if (silhouettes is null || silhouettes.Length == 0) continue;
            silhouetteSegments.TryGetValue(index, out var segments);
            var grid = new Dictionary<(long, long), List<int>>();
            if (segments is not null)
                for (int s = 0; s < segments.Count; s++)
                {
                    var g = segments[s]; int steps = Math.Max(1, (int)(Math.Sqrt((g.Bx - g.Ax) * (g.Bx - g.Ax) + (g.By - g.Ay) * (g.By - g.Ay)) / reach) + 1);
                    for (int k = 0; k <= steps; k++)
                    {
                        var cell = ((long)Math.Floor((g.Ax + (g.Bx - g.Ax) * k / steps) / reach), (long)Math.Floor((g.Ay + (g.By - g.Ay) * k / steps) / reach));
                        if (!grid.TryGetValue(cell, out var list)) grid.Add(cell, list = new());
                        if (list.Count == 0 || list[^1] != s) list.Add(s);
                    }
                }
            VisibilityClass Nearest(double x, double y)
            {
                double best = reach; var kind = VisibilityClass.Unresolved; long cx = (long)Math.Floor(x / reach), cy = (long)Math.Floor(y / reach);
                for (long i = cx - 1; i <= cx + 1; i++) for (long j = cy - 1; j <= cy + 1; j++)
                    if (grid.TryGetValue((i, j), out var list))
                        foreach (int s in list)
                        {
                            var g = segments![s]; double dx = g.Bx - g.Ax, dy = g.By - g.Ay, den = dx * dx + dy * dy;
                            double t = den == 0 ? 0 : Math.Clamp(((x - g.Ax) * dx + (y - g.Ay) * dy) / den, 0, 1);
                            double d = Math.Sqrt(Math.Pow(x - g.Ax - dx * t, 2) + Math.Pow(y - g.Ay - dy * t, 2));
                            // Visible wins a near tie, as at the drawing locus.
                            if (d < best - tol || d <= best + tol && g.Class == VisibilityClass.Visible && kind != VisibilityClass.Visible) { best = Math.Min(best, d); kind = g.Class; }
                        }
                return kind;
            }
            foreach (var silhouette in silhouettes)
            {
                if (silhouette.Curve is not { } curve) continue;
                double length = curve.GetLength(); if (length <= tol) continue;
                int count = Math.Max(2, (int)Math.Ceiling(length / Math.Max(eps, 4 * tol)));
                var parameters = curve.DivideByCount(count, true);
                if (parameters is null) continue;
                if (curve.IsClosed) parameters = parameters.Append(curve.Domain.Max).ToArray();
                VisibilityClass? previous = null; double start = parameters[0];
                for (int i = 1; i < parameters.Length; i++)
                {
                    var v = Project(curve.PointAt((parameters[i - 1] + parameters[i]) / 2));
                    var kind = Nearest(v.X, v.Y);
                    double piece = length / count; if (kind == VisibilityClass.Unresolved) unmatched += piece; else matched += piece;
                    if (previous is { } old && old != kind)
                    {
                        using var part = curve.Trim(start, parameters[i - 1]);
                        if (part is not null) pieces.Add(new { c = Class(old), s = items[index].Id, p = Flat(part) });
                        start = parameters[i - 1];
                    }
                    previous = kind;
                }
                if (previous is { } last)
                {
                    using var part = curve.Trim(start, parameters[^1]);
                    if (part is not null) pieces.Add(new { c = Class(last), s = items[index].Id, p = Flat(part) });
                }
                curve.Dispose();
            }
        }
        var smoothSeconds = smoothTimer.Elapsed.TotalSeconds - silhouetteSeconds;
        File.WriteAllText(Path.Combine(output, name + "-smooth-all.json"), JsonSerializer.Serialize(new { tolerance = tol, planar2d = true, pieces }));
        for (int index = 0; index < items.Length; index++) if (breps[index] is { } b && !ReferenceEquals(b, items[index].Geometry)) b.Dispose();

        double total = meshSeconds + topologySeconds + mapSeconds + solveSeconds;
        Console.WriteLine($"--- {stem} [mesh x{multiple:G} = {eps:G4}] total {total:F3}s (mesh {meshSeconds:F3} topology {topologySeconds:F3} edge map {mapSeconds:F3} kernel {solveSeconds:F3}, of which face preparation {prepareSeconds:F3}) + smooth recovery {smoothSeconds:F3}s + Rhino silhouettes {silhouetteSeconds:F3}s | product base {baseWall:F3}s (native {baseNative:F3}s)");
        Console.WriteLine($"    meshed sources {meshedSources} ({kinkSplit} split at kinks), unsupported {unsupported.Count}; triangles {triangles}; faces {faces.Count} (planar {planarFaces}); candidate edges {edges.Count} (planar {meta.Count(m => m.Kind == 0)}, Brep edge {meta.Count(m => m.Kind == 1)}, silhouette {meta.Count(m => m.Kind == 2)}, curve {meta.Count(m => m.Kind == 3)}); unmapped naked mesh edges {nakedMesh}; measured chord deviation worst {worstChord:G4} ({worstChord / eps:F2} eps), {chordOver}/{chordSamples} samples over eps; kernel tests {result.FaceTests}, exact fallbacks {result.ExactFallbacks}; diagnostics {string.Join("; ", result.Diagnostics)}");
        Console.WriteLine($"    lengths: {string.Join(", ", lengths.OrderBy(l => l.Key).Select(l => $"{l.Key}={l.Value:F2}"))}; exact silhouette length matched {matched:F2}, unmatched {unmatched:F2}");
        rows.Add(new { name = "mesh", multiple, eps, total, meshSeconds, topologySeconds, mapSeconds, solveSeconds, prepareSeconds, smoothSeconds, silhouetteSeconds, baseWall, baseNative, meshedSources, kinkSplit, unsupported = unsupported.ToArray(),
            triangles, faces = faces.Count, planarFaces, candidates = edges.Count, nakedMesh, worstChord, chordOver, chordSamples, faceTests = result.FaceTests, exactFallbacks = result.ExactFallbacks, lengths, matched, unmatched,
            poly = name + "-poly-all.json", smooth = name + "-smooth-all.json" });
        return unsupported;
    }
}
