using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
#if !LICENSE_FREE
using Rhino.DocObjects;
#endif
using Rhino.FileIO;
using Rhino.Geometry;

namespace M2DPlus.HcPerf;

public static class Bench
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
#if !LICENSE_FREE
    public static void NativeGate(string path, string viewName, string output)
    {
        using var model = File3dm.Read(path);
        double tolerance = model.Settings.ModelAbsoluteTolerance;
        var n = viewName == "Top" ? Vector3d.ZAxis : viewName == "North" ? Vector3d.YAxis : viewName == "Front" ? -Vector3d.YAxis : new Vector3d(-1, -1, 1); n.Unitize();
        var x = Vector3d.CrossProduct(Math.Abs(n.Z) > .99 ? Vector3d.YAxis : Vector3d.ZAxis, n); x.Unitize();
        var view = new Plane(Point3d.Origin, x, Vector3d.CrossProduct(n, x));
        var scene = new PlanarExtraction();
        var leaves = PlanarExtraction.Leaves(model).Where(l => scene.Add(l.Geometry, l.Transform, view, tolerance, l.Source)).ToArray();
        var loci = Native(leaves, view, tolerance, out var diagnostics, output + ".slots.json");
        File.WriteAllText(output + ".native.json", JsonSerializer.Serialize(loci));
        File.WriteAllText(output + ".scene.json", JsonSerializer.Serialize(new { scene.Faces, scene.Edges, scene.Excluded, tolerance = model.Settings.ModelAbsoluteTolerance, metersToModelUnits = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem) }));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        File.WriteAllText(output + ".native-meta.json", JsonSerializer.Serialize(new { diagnostics,
            modelSha256 = Hash(path), model = path, view = viewName, tolerance,
            nativeSha256 = Hash(output + ".native.json"), slotsSha256 = Hash(output + ".slots.json"), sceneSha256 = Hash(output + ".scene.json"),
            rhinoCommonSha256 = Hash(typeof(RhinoApp).Assembly.Location),
            rhino = RhinoApp.Version.ToString(), runtime = System.Environment.Version.ToString(),
            priority = Process.GetCurrentProcess().PriorityClass.ToString(),
            multipleThreads = false, flatten = false, includeHidden = true, includeTangents = false, includeSeams = false,
            excluded = scene.Excluded, objects = scene.EligibleObjects, purpose = "correctness only; no suite timing mutex; normal priority; serial owned process" }));
    }
#endif
    public static string Run(string path, string viewName, string output, bool oracle, bool native, int repeats, int threads)
    {
        long peakManaged = 0, peakPrivate = 0;
        using var memorySampler = new System.Threading.Timer(_ =>
        {
            long m = GC.GetTotalMemory(false); Interlocked.Exchange(ref peakManaged, Math.Max(Interlocked.Read(ref peakManaged), m));
            using var p = Process.GetCurrentProcess(); p.Refresh();
            Interlocked.Exchange(ref peakPrivate, Math.Max(Interlocked.Read(ref peakPrivate), p.PrivateMemorySize64));
        }, null, 0, 100);
        var watch = Stopwatch.StartNew();
        using var model = File3dm.Read(path) ?? throw new IOException(path);
        double readSeconds = watch.Elapsed.TotalSeconds;
        double tolerance = model.Settings.ModelAbsoluteTolerance;
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, model.Settings.ModelUnitSystem);
        Vector3d normal = viewName switch { "Top" => Vector3d.ZAxis, "North" => Vector3d.YAxis, "Front" => -Vector3d.YAxis, _ => new(-1, -1, 1) };
        normal.Unitize();
        Vector3d right = Vector3d.CrossProduct(Math.Abs(normal.Z) > .99 ? Vector3d.YAxis : Vector3d.ZAxis, normal); right.Unitize();
        var view = new Plane(Point3d.Origin, right, Vector3d.CrossProduct(normal, right));
        var leaves = PlanarExtraction.Leaves(model).ToArray();
        var times = new List<double>(); var managed = new List<long>();
        PlanarExtraction extraction = null!; VisibilityResult result = null!; Locus[] loci = [];
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        for (int run = 0; run < repeats; run++)
        {
            watch.Restart(); extraction = new(); eligible.Clear();
            foreach (var leaf in leaves)
                if (extraction.Add(leaf.Geometry, leaf.Transform, view, tolerance, leaf.Source)) eligible.Add(leaf.Source);
            result = EdgeFaceKernel.Solve(extraction.Faces, extraction.Edges, new(tolerance, threads));
            loci = VisibilityLoci.Create(extraction.Edges, result, tolerance);
            times.Add(watch.Elapsed.TotalSeconds); managed.Add(GC.GetTotalMemory(false));
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(path)} {viewName} repeat {run}: {times[^1]:F3}s {extraction.Edges.Count} edges {result.FaceTests} pairs {result.ExactFallbacks} exact");
            if (times[^1] >= 10) break;
        }
        var failures = new List<string>(); var details = new Dictionary<string, object>();
        if (result.Intervals.Any(i => i.Class == VisibilityClass.Unresolved)) failures.Add("unresolved planar intervals");
        if (oracle)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                watch.Restart();
                var truth = ExactVisibilityOracle.Solve(extraction.Faces, extraction.Edges, tolerance, limit.Token, 8);
                details["oracleSeconds"] = watch.Elapsed.TotalSeconds;
                CheckIntervals(truth, result, extraction.Edges, tolerance, failures);
            }
            catch (OperationCanceledException) { failures.Add("exact oracle exceeded 10 minute guard (gate incomplete)"); }
        }
        // Face permutation and view-coordinate rigid translation must preserve every candidate interval.
        var permutedEdges = extraction.Edges.Select(e => e with { AdjacentFaces=e.AdjacentFaces.Select(i=>extraction.Faces.Count-1-i).ToArray() }).ToArray();
        var permuted = EdgeFaceKernel.Solve(extraction.Faces.AsEnumerable().Reverse().ToArray(), permutedEdges, new(tolerance, threads));
        CheckIntervals(result, permuted, extraction.Edges, tolerance, failures);
        // Signed coordinate permutation is a rigid transform that does not round the
        // supplied binary64 scene. General rounded translations are a separate stress test.
        Vertex Move(Vertex p) => new(-p.Y, p.X, p.Z);
        var movedFaces = extraction.Faces.Select(f => f with { Loops=f.Loops.Select(l => l.Select(Move).ToArray()).ToArray(),OutwardNormal=f.OutwardNormal is {} n?Move(n):null }).ToArray();
        var movedEdges = extraction.Edges.Select(e => e with { A = Move(e.A), B = Move(e.B) }).ToArray();
        var moved = EdgeFaceKernel.Solve(movedFaces, movedEdges, new(tolerance, threads));
        CheckIntervals(result, moved, extraction.Edges, tolerance, failures);
        double? nativeSeconds = null;
        if (native)
        {
#if !LICENSE_FREE
            // One serial correctness solve; deliberately no timing measurement.
            var nativeLoci = Native(leaves.Where(l => eligible.Contains(l.Source)).ToArray(), view, tolerance, out var diag, output + ".slots.json");
            details["nativeDiagnostics"] = diag;
            if (diag.Count > 0) failures.AddRange(diag);
            Console.WriteLine($"native {viewName}: correctness solve completed");
            var missing = VisibilityLoci.Compare(nativeLoci, loci, tolerance, .05 * unit);
            var extra = VisibilityLoci.Compare(loci, nativeLoci, tolerance, .05 * unit);
            details["nativeMissing"] = missing; details["nativeExtra"] = extra;
            if (missing.Missed + extra.Missed > 0) failures.Add($"unexplained native locus disagreement: missing={missing.Missed}, extra={extra.Missed}");
            File.WriteAllText(output + ".native.json", JsonSerializer.Serialize(nativeLoci, Json));
#else
            failures.Add("Native comparison unavailable in licence-free reader");
#endif
        }
        var process = Process.GetCurrentProcess(); process.Refresh();
        var row = new
        {
            model = path, modelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), view = viewName,
            engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EdgeFaceKernel).Assembly.Location))),
            oracleSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ExactVisibilityOracle).Assembly.Location))),
            rhino = typeof(File3dm).Assembly.GetName().Version?.ToString(), runtime = System.Environment.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            tolerance, threads, readSeconds, seconds = Median(times), repeatSeconds = times, nativeSeconds,
            nativeRatio = nativeSeconds / Median(times), managedBytes = Math.Max(peakManaged, managed.Max()), privateBytes = Math.Max(peakPrivate, process.PrivateMemorySize64),
            peakWorkingSetBytes = process.PeakWorkingSet64, objects = extraction.EligibleObjects, excluded = extraction.Excluded,
            edges = extraction.Edges.Count, faces = extraction.Faces.Count, result.FaceTests, result.ExactFallbacks,
            visibleLength = loci.Where(l => l.Class == VisibilityClass.Visible).Sum(l => l.Length),
            hiddenLength = loci.Where(l => l.Class == VisibilityClass.Hidden).Sum(l => l.Length),
            gate = new { passed = failures.Count == 0, failures }, oracleRequested = oracle, nativeRequested = native, details,
            mixedPolicy = "Whole-view native fallback; benchmark extraction excludes unsupported sources explicitly."
        };
        File.WriteAllText(output + ".loci.json", JsonSerializer.Serialize(loci, Json));
        string json = JsonSerializer.Serialize(row, Json); File.WriteAllText(output + ".json", json); return json;
    }

    internal static void CheckIntervals(VisibilityResult a, VisibilityResult b, IReadOnlyList<CandidateEdge> edges, double tolerance, List<string> failures)
    {
        var aa = a.Intervals.GroupBy(i => i.Edge).ToDictionary(g => g.Key, g => g.ToArray());
        var bb = b.Intervals.GroupBy(i => i.Edge).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (int key in aa.Keys.Union(bb.Keys))
        {
            var left = aa.GetValueOrDefault(key) ?? []; var right = bb.GetValueOrDefault(key) ?? [];
            double dx = edges[key].A.X - edges[key].B.X, dy = edges[key].A.Y - edges[key].B.Y, length = Math.Sqrt(dx * dx + dy * dy);
            bool Uncovered(EdgeInterval[] from, EdgeInterval[] to)
            {
                foreach (var span in from)
                {
                    if ((span.End - span.Start) * length <= tolerance) continue;
                    double cursor = span.Start;
                    foreach (var target in to.Where(t => t.Class == span.Class))
                    {
                        if (target.End < cursor) continue;
                        if ((target.Start - cursor) * length > tolerance) break;
                        cursor = Math.Max(cursor, target.End);
                    }
                    if ((span.End - cursor) * length > tolerance) return true;
                }
                return false;
            }
            if (Uncovered(left, right) || Uncovered(right, left))
            {
                failures.Add($"interval mismatch edge {key}: {JsonSerializer.Serialize(left)} vs {JsonSerializer.Serialize(right)}");
                if (failures.Count > 100) return;
            }
        }
    }

#if !LICENSE_FREE
    private static Locus[] Native((GeometryBase Geometry, Transform Transform, string Source)[] leaves, Plane view, double tolerance, out List<string> diagnostics, string? slotsPath = null)
    {
        diagnostics = []; var output = new List<Locus>(); var owned = new List<GeometryBase>(); var slots = new List<object>();
        // The empty planar subset has identical empty output on both sides. Rhino
        // rejects an HLD call with no geometry; exclusions remain in NativeGate's census.
        if (leaves.Length == 0)
        {
            if (slotsPath is not null) File.WriteAllText(slotsPath, "[]");
            return [];
        }
        using var vp = new ViewportInfo(); vp.IsParallelProjection = true;
        var bounds = BoundingBox.Empty;
        foreach (var leaf in leaves) { var b = leaf.Geometry.GetBoundingBox(true); b.Transform(leaf.Transform); bounds.Union(b); }
        double distance = bounds.IsValid ? Math.Max(10, bounds.Diagonal.Length * 2) : 10;
        var center = bounds.IsValid ? bounds.Center : Point3d.Origin;
        vp.SetCameraLocation(center + view.ZAxis * distance); vp.SetCameraDirection(-view.ZAxis); vp.SetCameraUp(view.YAxis);
        vp.SetFrustum(-distance, distance, -distance, distance, .001, distance * 4);
        var parameters = new HiddenLineDrawingParameters { AbsoluteTolerance = tolerance, Flatten = false,
            IncludeHiddenCurves = true, IncludeTangentEdges = false, IncludeTangentSeams = false };
        parameters.SetViewport(vp);
        try
        {
            foreach (var leaf in leaves)
            {
                GeometryBase shape = leaf.Geometry;
                if (shape is SubD subd) { shape = subd.ToBrep(); owned.Add(shape); }
                if (!parameters.AddGeometry(shape, leaf.Transform, leaf.Source)) diagnostics.Add("Native rejected " + leaf.Source);
            }
            using var drawing = HiddenLineDrawing.Compute(parameters, false) ?? throw new InvalidOperationException("Null HLD");
            if (!drawing.WorldToHiddenLine.TryGetInverse(out var inverse)) throw new InvalidOperationException("Singular HLD view");
            var flatten = Transform.PlaneToPlane(view, Plane.WorldXY) * inverse;
            foreach (var segment in drawing.Segments)
            {
                var visibility = segment.SegmentVisibility;
                if (visibility == HiddenLineDrawingSegment.Visibility.Unset)
                {
                    slots.Add(InspectUnset(segment, flatten));
                    diagnostics.Add("Native Unset proxy"); continue;
                }
                if (segment.ParentCurve is null || segment.CurveGeometry is null) { diagnostics.Add("Invalid native slot"); continue; }
                using var c = segment.CurveGeometry.DuplicateCurve(); c.Transform(flatten);
                using var poly = c.ToPolyline(tolerance * .25, Math.PI / 90, 0, 0);
                if (poly is null || !poly.TryGetPolyline(out var points)) { diagnostics.Add("Native polyline failed"); continue; }
                string source = segment.ParentCurve.SourceObject?.Tag?.ToString() ?? "<none>";
                source += "|" + segment.ParentCurve.SilhouetteType;
                slots.Add(new { visibility = visibility.ToString(), source, points = points.Select(p => new Vertex(p.X, p.Y, p.Z)).ToArray() });
                if (visibility is not (HiddenLineDrawingSegment.Visibility.Visible or HiddenLineDrawingSegment.Visibility.Hidden)) continue;
                for (int i = 1; i < points.Count; i++) output.Add(new(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y,
                    visibility == HiddenLineDrawingSegment.Visibility.Visible ? VisibilityClass.Visible : VisibilityClass.Hidden, source));
            }
        }
        finally { foreach (var g in owned) g.Dispose(); }
        if (slotsPath is not null) File.WriteAllText(slotsPath, JsonSerializer.Serialize(slots));
        return output.ToArray();
    }

    private static Dictionary<string, object?> InspectUnset(HiddenLineDrawingSegment segment, Transform projection)
    {
        var evidence = new Dictionary<string, object?> { ["visibility"] = "Unset", ["index"] = segment.Index };
        try
        {
            var parent = segment.ParentCurve;
            evidence["hasParent"] = parent is not null;
            if (segment.Index < 0 && parent is null) { evidence["kind"] = "empty native slot"; return evidence; }
            evidence["source"] = parent?.SourceObject?.Tag?.ToString();
            var proxy = segment.CurveGeometry;
            evidence["hasGeometry"] = proxy is not null;
            evidence["valid"] = proxy?.IsValid == true;
            evidence["kind"] = "nonzero or ambiguous native record";
            if (proxy?.IsValid == true)
            {
                var bounds = proxy.GetBoundingBox(projection);
                double length = proxy.GetLength();
                evidence["length"] = double.IsFinite(length) ? length : null;
                if (bounds.IsValid)
                {
                    evidence["bounds"] = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z };
                    if (bounds.Diagonal.Length == 0 && length == 0) evidence["kind"] = "exactly point-sized native curve";
                }
                var a = proxy.PointAtStart; var b = proxy.PointAtEnd; a.Transform(projection); b.Transform(projection);
                if (a.IsValid && b.IsValid) evidence["points"] = new[] { new Vertex(a.X, a.Y, a.Z), new Vertex(b.X, b.Y, b.Z) };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException or NotSupportedException)
        { evidence["error"] = ex.GetType().Name + ": " + ex.Message; }
        return evidence;
    }
#endif
    private static double Median(List<double> x) => x.Order().ElementAt(x.Count / 2);
}
