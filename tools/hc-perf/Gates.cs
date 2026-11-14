using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.HcPerf;

public static class Gates
{
    public static void RoundedRigid(string path, string viewName, string output)
    {
        using var model = File3dm.Read(path);
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem), tolerance = model.Settings.ModelAbsoluteTolerance;
        var n = viewName == "North" ? Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
        var x = Vector3d.CrossProduct(Vector3d.ZAxis, n); x.Unitize();
        var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x)); var scene = new PlanarExtraction();
        foreach (var l in PlanarExtraction.Leaves(model)) scene.Add(l.Geometry, l.Transform, view, tolerance, l.Source);
        var original = EdgeFaceKernel.Solve(scene.Faces, scene.Edges, new(tolerance, 8));
        Vertex Move(Vertex p) => new(-p.Y + 13 * unit, p.X - 7 * unit, p.Z + 5 * unit);
        var faces = scene.Faces.Select(f => new PlanarFace(f.Loops.Select(l => l.Select(Move).ToArray()).ToArray())).ToArray();
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

    public static void Tiles(string path, string viewName, string output)
    {
        using var model = File3dm.Read(path);
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem);
        double tolerance = model.Settings.ModelAbsoluteTolerance;
        var n = viewName == "North" ? Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
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
            var watch = Stopwatch.StartNew();
            var kernel = EdgeFaceKernel.Solve(faces, edges, new(tolerance, 8));
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                var exact = ExactVisibilityOracle.Solve(faces, edges, tolerance, limit.Token, 8);
                Bench.CheckIntervals(exact, kernel, edges, tolerance, failures);
                var expected = VisibilityLoci.Create(edges, exact, tolerance); var actual = VisibilityLoci.Create(edges, kernel, tolerance);
                var a = VisibilityLoci.Compare(expected, actual, tolerance, .05 * unit);
                var b = VisibilityLoci.Compare(actual, expected, tolerance, .05 * unit);
                if (a.Missed + b.Missed > 0) failures.Add($"tile {u}/{v} mismatch: {a.Missed}/{b.Missed}");
                tiles.Add(new { cx, cy, width = 20 * unit, edges = edges.Count, faces = faces.Length, oraclePairs = exact.FaceTests,
                    seconds = watch.Elapsed.TotalSeconds, missing = a, extra = b });
            }
            catch (OperationCanceledException) { failures.Add($"tile {u}/{v} oracle exceeded 10 minute guard"); }
            Console.WriteLine($"tile {u}/{v} {edges.Count} edges x {faces.Length} faces in {watch.Elapsed.TotalSeconds:F1}s");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failures.Count == 0 && tiles.Count == 4, failures, tiles }));
        }
    }
}
