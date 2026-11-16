using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.HcPerf;

public static class Gates
{
    private static (PlanarFace[] Faces,CandidateEdge[] Edges) Subset(IReadOnlyList<PlanarFace> all,PlanarFace[] selected,CandidateEdge[] edges)
    {
        var selectedSet=selected.ToHashSet(ReferenceEqualityComparer.Instance);
        var ids=all.Select((f,i)=>(f,i)).Where(p=>selectedSet.Contains(p.f)).Select(p=>p.i)
            .Concat(edges.SelectMany(e=>e.AdjacentFaces)).Distinct().Order().ToArray();
        var map=ids.Select((id,i)=>(id,i)).ToDictionary(p=>p.id,p=>p.i);
        return (ids.Select(i=>all[i]).ToArray(),edges.Select(e=>e with {AdjacentFaces=e.AdjacentFaces.Select(i=>map[i]).ToArray()}).ToArray());
    }
    public static void RoundedRigid(string path, string viewName, string output)
    {
        using var model = File3dm.Read(path);
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem), tolerance = model.Settings.ModelAbsoluteTolerance;
        var n = viewName == "North" ? Vector3d.YAxis : viewName == "Front" ? -Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
        var x = Vector3d.CrossProduct(Vector3d.ZAxis, n); x.Unitize();
        var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x)); var scene = new PlanarExtraction();
        foreach (var l in PlanarExtraction.Leaves(model)) scene.Add(l.Geometry, l.Transform, view, tolerance, l.Source);
        var original = EdgeFaceKernel.Solve(scene.Faces, scene.Edges, new(tolerance, 8));
        Vertex Move(Vertex p) => new(-p.Y + 13 * unit, p.X - 7 * unit, p.Z + 5 * unit);
        var faces = scene.Faces.Select(f => f with {Loops=f.Loops.Select(l => l.Select(Move).ToArray()).ToArray(),OutwardNormal=f.OutwardNormal is {} normal?new(-normal.Y,normal.X,normal.Z):null}).ToArray();
        var edges = scene.Edges.Select(e => e with { A = Move(e.A), B = Move(e.B) }).ToArray();
        var moved = EdgeFaceKernel.Solve(faces, edges, new(tolerance, 8));
        var a = VisibilityLoci.Create(scene.Edges, original, tolerance);
        var b = VisibilityLoci.Create(edges, moved, tolerance).Select(l => new Locus(l.Y0 + 7 * unit, 13 * unit - l.X0,
            l.Y1 + 7 * unit, 13 * unit - l.X1, l.Class, l.Source)).ToArray();
        var missing = VisibilityLoci.Compare(a, b, tolerance, .05 * unit); var extra = VisibilityLoci.Compare(b, a, tolerance, .05 * unit);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = missing.Missed + extra.Missed == 0, missing, extra }));
    }
    public static void Census(string directory, string output)
    {
        var rows = new List<object>();
        foreach (string path in Directory.GetFiles(directory, "*.3dm").Order(StringComparer.Ordinal))
        {
            using var model = File3dm.Read(path);
            var views = new List<object>();
            foreach (string name in new[] { "Top", "Front", "SW" })
            {
                var n = name == "Top" ? Vector3d.ZAxis : name == "Front" ? -Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
                var x = Vector3d.CrossProduct(Math.Abs(n.Z) > .99 ? Vector3d.YAxis : Vector3d.ZAxis, n); x.Unitize();
                var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x)); var scene = new PlanarExtraction();
                foreach (var l in PlanarExtraction.Leaves(model)) scene.Add(l.Geometry, l.Transform, view, model.Settings.ModelAbsoluteTolerance, l.Source);
                views.Add(new { view = name, scene.EligibleObjects, scene.OtherObjects, faces = scene.Faces.Count, edges = scene.Edges.Count,
                    excludedTypes = scene.Excluded.GroupBy(s => s[(s.LastIndexOf(':') + 1)..]).ToDictionary(g => g.Key, g => g.Count()),
                    route = scene.OtherObjects > 0 ? "whole-view native required; no partial drawing published" : "eligible planar view",
                    mixedSeconds = (double?)null, timingStatus = "census only; requires a separate licensed native benchmark" });
            }
            rows.Add(new { model = path, views });
            File.WriteAllText(output, JsonSerializer.Serialize(rows));
        }
    }
    public static string Fable(string prefix)
    {
        var prototype = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(prefix + ".loci.json"))!;
        var candidates = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(prefix + ".loci.json.candidates.json"))!;
        var native = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(prefix + ".native.json"))!;
        var v = VisibilityLoci.Union(prototype, .0001).Where(l => l.Class == VisibilityClass.Visible).ToArray();
        var n = native.Where(l => l.Class == VisibilityClass.Visible).ToArray();
        var missing = VisibilityLoci.Compare(n, v, .0001, .01);
        var absentCandidates = VisibilityLoci.Compare(n, candidates, .0001, .01);
        var extra = VisibilityLoci.Compare(v, n, .0001, .01);
        return JsonSerializer.Serialize(new { nativeVisibleLength = n.Sum(l => l.Length), prototypeVisibleLength = v.Sum(l => l.Length),
            missing, extra, absentCandidates, visibilityDisagreementLength = missing.SampledMissingLength - absentCandidates.SampledMissingLength });
    }
    public static string CompareFiles(string a, string b, double tolerance, double spacing)
    {
        var left = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(a))!;
        var right = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(b))!;
        var missing = VisibilityLoci.Compare(left, right, tolerance, spacing);
        var extra = VisibilityLoci.Compare(right, left, tolerance, spacing);
        return JsonSerializer.Serialize(new { passed = missing.Missed + extra.Missed == 0, missing, extra });
    }

    public static void Scene(string path, string viewName, string output)
    {
        using var model = File3dm.Read(path);
        var n = viewName == "Top" ? Vector3d.ZAxis : viewName == "North" ? Vector3d.YAxis : viewName == "Front" ? -Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
        var x = Vector3d.CrossProduct(Math.Abs(n.Z) > .99 ? Vector3d.YAxis : Vector3d.ZAxis, n); x.Unitize();
        var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x)); var scene = new PlanarExtraction();
        foreach (var l in PlanarExtraction.Leaves(model)) scene.Add(l.Geometry, l.Transform, view, model.Settings.ModelAbsoluteTolerance, l.Source);
        File.WriteAllText(output, JsonSerializer.Serialize(new { scene.Faces, scene.Edges, scene.Excluded, tolerance = model.Settings.ModelAbsoluteTolerance, metersToModelUnits = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem) }));
    }

    public static void NativeOracle(string prefix, string output)
    {
        using var scene = JsonDocument.Parse(File.ReadAllText(prefix + ".scene.json"));
        var faces = scene.RootElement.GetProperty("Faces").Deserialize<PlanarFace[]>()!;
        var edges = scene.RootElement.GetProperty("Edges").Deserialize<CandidateEdge[]>()!;
        using var comparison = JsonDocument.Parse(File.ReadAllText(prefix + ".canonical-comparison.json"));
        var disputed = new[] { "missing", "extra" }.SelectMany(k => comparison.RootElement.GetProperty(k)
            .GetProperty("FailedSegments").Deserialize<Locus[]>()!).ToArray();
        double tolerance = scene.RootElement.TryGetProperty("tolerance", out var toleranceValue) ? toleranceValue.GetDouble() : .0001;
        bool Meets(CandidateEdge e, Locus l)
        {
            double dx = e.B.X - e.A.X, dy = e.B.Y - e.A.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length == 0) return false;
            double Cross(double x, double y) => Math.Abs(dx * (y - e.A.Y) - dy * (x - e.A.X)) / length;
            if (Cross(l.X0, l.Y0) > tolerance || Cross(l.X1, l.Y1) > tolerance) return false;
            double At(double x, double y) => ((x - e.A.X) * dx + (y - e.A.Y) * dy) / length;
            double a = At(l.X0, l.Y0), b = At(l.X1, l.Y1);
            return Math.Max(a, b) >= -tolerance && Math.Min(a, b) <= length + tolerance;
        }
        var selected = edges.Where(e => disputed.Any(l => Meets(e, l))).ToArray();
        // Conservative projected bounds only: no potentially occluding face omitted.
        var retained = faces.Where(f =>
        {
            var p = f.Loops.SelectMany(l => l).ToArray();
            double x0 = p.Min(v => v.X), x1 = p.Max(v => v.X), y0 = p.Min(v => v.Y), y1 = p.Max(v => v.Y);
            return selected.Any(e => Math.Min(e.A.X, e.B.X) <= x1 && Math.Max(e.A.X, e.B.X) >= x0
                && Math.Min(e.A.Y, e.B.Y) <= y1 && Math.Max(e.A.Y, e.B.Y) >= y0);
        }).ToArray();
        (retained,selected)=Subset(faces,retained,selected);
        var watch = Stopwatch.StartNew();
        var actual = EdgeFaceKernel.Solve(retained, selected, new(tolerance, 8));
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var expected = ExactVisibilityOracle.Solve(retained, selected, tolerance, limit.Token, 8);
        var failures = new List<string>(); Bench.CheckIntervals(expected, actual, selected, tolerance, failures);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failures.Count == 0, failures,
            edges = selected.Length, faces = retained.Length, expected.FaceTests, seconds = watch.Elapsed.TotalSeconds,
            selectedEdges = selected, oracleIntervals = expected.Intervals }));
    }

    public static string CompareNative(string a, string b, double tolerance, double spacing)
    {
        var raw = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(a))!;
        var kernel = JsonSerializer.Deserialize<Locus[]>(File.ReadAllText(b))!;
        var canonical = VisibilityLoci.Union(raw, tolerance);
        var missing = VisibilityLoci.Compare(canonical, kernel, tolerance, spacing);
        var extra = VisibilityLoci.Compare(kernel, canonical, tolerance, spacing);
        return JsonSerializer.Serialize(new { passed = missing.Missed + extra.Missed == 0,
            rawCount = raw.Length, canonicalCount = canonical.Length, nativeCanonical = canonical, missing, extra });
    }

    public static void Tiles(string path, string viewName, string output, string oracleFingerprint = "", string? referenceDirectory = null)
    {
        using var model = File3dm.Read(path);
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem);
        double tolerance = model.Settings.ModelAbsoluteTolerance;
        var n = viewName == "North" ? Vector3d.YAxis : viewName == "Front" ? -Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
        var x = Vector3d.CrossProduct(Vector3d.ZAxis, n); x.Unitize();
        var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x));
        var scene = new PlanarExtraction();
        foreach (var l in PlanarExtraction.Leaves(model)) scene.Add(l.Geometry, l.Transform, view, tolerance, l.Source);
        double x0 = scene.Edges.Min(e => Math.Min(e.A.X, e.B.X)), x1 = scene.Edges.Max(e => Math.Max(e.A.X, e.B.X));
        double y0 = scene.Edges.Min(e => Math.Min(e.A.Y, e.B.Y)), y1 = scene.Edges.Max(e => Math.Max(e.A.Y, e.B.Y));
        var tiles = new List<object>(); var failures = new List<string>();
        foreach (double u in new[] { .25, .75 }) foreach (double v in new[] { .25, .75 })
        {
            double cx = x0 + (x1 - x0) * u, cy = y0 + (y1 - y0) * v, half = 10 * unit;
            var faces = scene.Faces.Where(f =>
            {
                var p = f.Loops.SelectMany(l => l).ToArray();
                return p.Min(t => t.X) <= cx + half && p.Max(t => t.X) >= cx - half
                    && p.Min(t => t.Y) <= cy + half && p.Max(t => t.Y) >= cy - half;
            }).ToArray();
            var edges = new List<CandidateEdge>();
            foreach (var edge in scene.Edges)
            {
                double lo = 0, hi = 1;
                bool Clip(double a, double d, double min, double max)
                {
                    if (d == 0) return a >= min && a <= max;
                    double s = (min - a) / d, t = (max - a) / d; if (s > t) (s, t) = (t, s);
                    lo = Math.Max(lo, s); hi = Math.Min(hi, t); return lo < hi;
                }
                if (!Clip(edge.A.X, edge.B.X - edge.A.X, cx - half, cx + half) || !Clip(edge.A.Y, edge.B.Y - edge.A.Y, cy - half, cy + half)) continue;
                Vertex At(double t) => new(edge.A.X + t * (edge.B.X - edge.A.X), edge.A.Y + t * (edge.B.Y - edge.A.Y), edge.A.Z + t * (edge.B.Z - edge.A.Z));
                edges.Add(edge with { A = At(lo), B = At(hi) });
            }
            var subset=Subset(scene.Faces,faces,edges.ToArray());faces=subset.Faces;edges=subset.Edges.ToList();
            var watch = Stopwatch.StartNew();
            var kernel = EdgeFaceKernel.Solve(faces, edges, new(tolerance, 8));
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                string inputSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { faces, edges, tolerance })));
                string referenceFile = output + $".{(int)(u * 100)}-{(int)(v * 100)}.oracle-reference.json";
                int drawableEdges = edges.Count(e => e.A.X != e.B.X || e.A.Y != e.B.Y);
                long fullPairCount = (long)faces.Length * drawableEdges;
                string? cachedFile = referenceDirectory is null ? null : Path.Combine(referenceDirectory, Path.GetFileName(referenceFile));
                VisibilityResult? exact = null; bool referenceReused = false;
                if (oracleFingerprint.Length > 0 && cachedFile is not null && File.Exists(cachedFile))
                {
                    using var cache = JsonDocument.Parse(File.ReadAllText(cachedFile)); var record = cache.RootElement;
                    if (record.GetProperty("schema").GetInt32() == 1 && record.GetProperty("inputSha256").GetString() == inputSha256
                        && record.GetProperty("oracleFingerprint").GetString() == oracleFingerprint
                        && record.GetProperty("FaceTests").GetInt64() == fullPairCount)
                    {
                        exact = new(record.GetProperty("intervals").Deserialize<EdgeInterval[]>()!, record.GetProperty("FaceTests").GetInt64(), 0, []);
                        referenceReused = true;
                    }
                }
                exact ??= ExactVisibilityOracle.Solve(faces, edges, tolerance, limit.Token, 8);
                if (exact.FaceTests != fullPairCount) throw new InvalidOperationException("Incomplete exact crop reference");
                if (referenceReused) File.Copy(cachedFile!, referenceFile, true);
                else File.WriteAllText(referenceFile, JsonSerializer.Serialize(new { schema = 1, inputSha256, oracleFingerprint,
                    drawableEdges, exact.FaceTests, intervals = exact.Intervals }));
                Bench.CheckIntervals(exact, kernel, edges, tolerance, failures);
                var expected = VisibilityLoci.Create(edges, exact, tolerance); var actual = VisibilityLoci.Create(edges, kernel, tolerance);
                var a = VisibilityLoci.Compare(expected, actual, tolerance, .05 * unit);
                var b = VisibilityLoci.Compare(actual, expected, tolerance, .05 * unit);
                if (a.Missed + b.Missed > 0) failures.Add($"tile {u}/{v} mismatch: {a.Missed}/{b.Missed}");
                tiles.Add(new { cx, cy, width = 20 * unit, edges = edges.Count, faces = faces.Length, oraclePairs = exact.FaceTests,
                    referenceReused, inputSha256, referenceFile = Path.GetFileName(referenceFile), seconds = watch.Elapsed.TotalSeconds, missing = a, extra = b });
            }
            catch (OperationCanceledException) { failures.Add($"tile {u}/{v} oracle exceeded 10 minute guard"); }
            Console.WriteLine($"tile {u}/{v} {edges.Count} edges x {faces.Length} faces in {watch.Elapsed.TotalSeconds:F1}s");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failures.Count == 0 && tiles.Count == 4, failures, tiles }));
        }
    }
}
