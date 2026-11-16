using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.PerfF;

/// <summary>
/// PERF-F research harness. One owned headless Rhino.Inside process; runs variants back to back on the
/// same prepared scene and dumps the complete drawing for an independent comparison (compare.py).
/// Usage: PerfF resolver model.3dm outdir view variant[,variant...] [variant...]
///   view: long | plan | trans | Top | Front | SW | ... (keyword views are uncut)
///   variant: any label; 'serial' forces one thread; 'audit-verdicts' compares world and definition-local verdicts
/// </summary>
internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var resolver = Assembly.LoadFrom(args[0]);
        resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
            .Invoke(null, new object[] { @"C:\Program Files\Rhino 8\System" });
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        if (args[1] == "native-point-witness") { NativePointWitnesses.Run(args[2]); return 0; }
        var file = args[1]; var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        var viewName = args[3]; var variants = args.Skip(4).ToArray();
        var timeout = double.Parse(Environment.GetEnvironmentVariable("PERFF_TIMEOUT") ?? "1500");
        var timer = Stopwatch.StartNew();
        using var document = RhinoDoc.OpenHeadless(file);
        var openSeconds = timer.Elapsed.TotalSeconds; timer.Restart();
        using var cache = new SceneCache(8L * 1024 * 1024 * 1024);
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromSeconds(300))) { }
        using var lease = capture.Accept(); var scene = lease.Value;
        var captureSeconds = timer.Elapsed.TotalSeconds; timer.Restart();
        var prepared = scene.Prepare(CancellationToken.None);
        var prepareSeconds = timer.Elapsed.TotalSeconds;
        var bounds = scene.Bounds;
        var curved = viewName.StartsWith("curved:", StringComparison.Ordinal); if (curved) viewName = viewName[7..];
        var spike = viewName.StartsWith("spike:", StringComparison.Ordinal); if (spike) viewName = viewName[6..];
        ViewRequest view = viewName switch
        {
            "North" => ViewRequest.Parse(-Vector3d.YAxis) with { Name = "North" },
            "long" => ViewRequest.Parse(new Plane(new Point3d(0, bounds.Center.Y, 0), Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Long-section" },
            "trans" => ViewRequest.Parse(new Plane(new Point3d(bounds.Center.X, 0, 0), Vector3d.YAxis, Vector3d.ZAxis)) with { Name = "Trans-section" },
            "plan" => ViewRequest.Parse(new Plane(new Point3d(0, 0, bounds.Min.Z + Math.Min(1.2, bounds.Diagonal.Z * .4)), Vector3d.XAxis, Vector3d.YAxis)) with { Name = "Plan" },
            _ when viewName.StartsWith("sidecar=", StringComparison.Ordinal) => Sidecar(file, viewName[8..]),
            _ => ViewRequest.Parse(viewName)
        };
        viewName = viewName.Replace("sidecar=", "").Replace(' ', '-');
        if (view.Error is not null) throw new ArgumentException(view.Error);
        var stem = Path.GetFileNameWithoutExtension(file) + "-" + viewName;
        Console.WriteLine($"{stem}: open {openSeconds:F2}s capture {captureSeconds:F2}s prepare {prepareSeconds:F2}s; {prepared.Count} leaves; bounds {bounds.Min} .. {bounds.Max}; tol {scene.Tolerance}");
        if (curved) { Curved.Run(scene, prepared, view, stem, output, variants, timeout); return 0; }
        if (spike) { Spike.Run(scene, prepared, view, stem, output, variants, timeout); return 0; }
#if !PERFF_BASELINE
        if (variants.Contains("section-probe")) { SectionProbe.Run(prepared, view, scene.Tolerance, Path.Combine(output, stem + "-section-probe.json")); return 0; }
        if (variants.Contains("audit-verdicts")) { Verdicts.Audit(scene, prepared, stem, output); return 0; }
        if (variants.Contains("stress-parallel")) { Verdicts.Stress(scene, prepared, stem, output); return 0; }
#endif
        var rows = new List<object>(); var run = 0;
        foreach (var variant in variants)
        {
            // A variant is a label. "serial" runs per-source section work on one thread (ignored by builds without the switch).
            // The scene-owned verdict cache makes every run after the first one warm.
            var parallelism = typeof(M2DPlus.Rhino.Sections.SectionRegionEngine).GetProperty("Parallelism", BindingFlags.NonPublic | BindingFlags.Static);
            var defaultParallelism = parallelism?.GetValue(null);
            if (variant.Split('+').Contains("serial")) parallelism?.SetValue(null, 1);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            GetSystemTimes(out var idle0, out var kernel0, out var user0);
            using var self = Process.GetCurrentProcess(); var cpu0 = self.TotalProcessorTime;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            timer.Restart(); DrawingBatch? batch = null; string? failure = null;
#if !PERFF_BASELINE
            var visibilityTrace = new List<object>();
            using var rayEvidence = new NativeRayEvidence();
            using var census = new UncertaintyCensus();
#endif
            try
            {
                // Direct view solve: bypasses the in-process admission cap (L exceeds it) and the tolerance census.
                run++;
                var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.NonPublic | BindingFlags.Static)!;
                var nativeSettings = new NativeSettings();
#if !PERFF_BASELINE
                if (variant == "census") nativeSettings = nativeSettings with { ObserveCompositionRecord = census.Capture,
                    ObserveNativeInputs = rayEvidence.Capture };
                if (variant == "audit-off") nativeSettings = nativeSettings with { Verification = new(MaxSamples:0) };
                if (variant == "trace-native") nativeSettings = nativeSettings with
                {
                    ObserveCompositionRecord = (source,edge,duplicate,kind,world) => visibilityTrace.Add(new { Source=source, Edge=edge, Duplicate=duplicate, Class=kind, World=world.ToJSON(new()) }),
                    ObserveNativeInputs = inputs => { rayEvidence.Capture(inputs); File.WriteAllText(Path.Combine(output,stem+"-native-inputs.json"),JsonSerializer.Serialize(inputs.Select(i => new { source=i.Source.Identity, path=i.Source.Path, geometry=GeometryFingerprint.Compute(i.Geometry), i.KernelCandidate, i.Far }))); },
                    ObservePlanarScene = (extraction,plane) => File.WriteAllText(Path.Combine(output,stem+"-planar-scene.json"),JsonSerializer.Serialize(new { extraction.Faces, extraction.Edges, origin=new[]{plane.OriginX,plane.OriginY,plane.OriginZ}, x=new[]{plane.XAxis.X,plane.XAxis.Y,plane.XAxis.Z}, y=new[]{plane.YAxis.X,plane.YAxis.Y,plane.YAxis.Z} }))
                };
#endif
                var drawn = (ViewDrawing)solve.Invoke(null, new object[] { scene, prepared, view, nativeSettings, limit.Token })!;
#if !PERFF_BASELINE
                if(variant=="census" && Environment.GetEnvironmentVariable("PERFF_HIDDEN_BASELINE") is { } hiddenBaseline)
                    HiddenAudit.Run(drawn,hiddenBaseline,Path.Combine(output,stem+"-hidden-audit.json"),scene.Bounds,prepared,view.Cut?.Plane);
                if (variant == "census") File.WriteAllText(Path.Combine(output,stem+"-causes.json"),JsonSerializer.Serialize(census.Measure(drawn)));
                if (variant == "census" && Environment.GetEnvironmentVariable("PERFF_UNCERTAINTY_BASELINE") is { } baseline)
                    rayEvidence.AuditRestored(drawn,baseline,Path.Combine(output,stem+"-restored-audit.json"),scene.Bounds);
                if (variant == "trace-native") File.WriteAllText(Path.Combine(output,stem+"-foreign-records.json"),JsonSerializer.Serialize(visibilityTrace));
                if (variant == "trace-native" && Environment.GetEnvironmentVariable("PERFF_RAY_POINTS") is { } rayPoints)
                    rayEvidence.Write(rayPoints,Path.Combine(output,stem+"-ray-evidence.json"),drawn.Frame.Plane,scene.Bounds,scene.Tolerance);
#endif
                batch = new DrawingBatch(new RequestIdentity(scene.Revision, "perf-f", variant, run), new List<ViewDrawing> { drawn });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { failure = $"timeout after {timeout} s"; }
            var wall = timer.Elapsed.TotalSeconds;
            GetSystemTimes(out var idle1, out var kernel1, out var user1); self.Refresh();
            var busy = 1 - (idle1 - idle0) / (double)Math.Max(1, kernel1 - kernel0 + user1 - user0);
            var ownCpu = (self.TotalProcessorTime - cpu0).TotalSeconds;
            var foreignLoad = Math.Max(0, busy * Environment.ProcessorCount - ownCpu / Math.Max(wall, 1e-9));
            parallelism?.SetValue(null, defaultParallelism);
            var verdictCache = scene.GetType().GetProperty("SectionVerdicts", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(scene);
            var verdicts = verdictCache?.GetType().GetProperty("Computations", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(verdictCache);
            string? hash = null; object? summary = null;
            if (batch is not null)
            {
                using (batch)
                {
                    var drawing = batch.Views[0];
                    var dump = Dump(drawing, scene.Tolerance);
                    var path = Path.Combine(output, $"{stem}-{run}-{variant.Replace('+', '_')}.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(dump));
                    hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                    summary = new { drawing.Seconds, drawing.Error, pieces = drawing.Items.Count,
                        classes = drawing.Items.GroupBy(p => p.Class).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                        regions = drawing.Regions.Count, hatches = drawing.Regions.Count(r => r.Region.Hatch is not null),
                        drawing.Report.Quarantined, drawing.Report.UncertainCount, drawing.Report.InvalidExtractionCount, phases = drawing.Phases.Seconds, dump = path };
                    failure = drawing.Error;
                }
            }
            Console.WriteLine($"--- {stem} run {run} [{variant}] wall {wall:F3}s ownCpu {ownCpu:F1}s machineBusy {busy:P0} foreignCores~{foreignLoad:F1} verdictsComputed {verdicts ?? "n/a"} hash {hash} {failure}");
            rows.Add(new { variant, run, wall, ownCpu, machineBusyFraction = busy, foreignCoresEstimate = foreignLoad, verdictsComputed = verdicts, hash, failure, summary });
            File.WriteAllText(Path.Combine(output, stem + "-timings.json"), JsonSerializer.Serialize(new
            {
                model = file, modelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), view = viewName,
                engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(NativeDrawing).Assembly.Location))),
                rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount,
                contention = "shared machine; two other long benchmark jobs running; see machineBusyFraction/foreignCoresEstimate per row",
                openSeconds, captureSeconds, prepareSeconds, leaves = prepared.Count, tolerance = scene.Tolerance, multipleThreads = false, rows
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    /// <summary>A cut plane from the model's JSON sidecar (the suite's own section definitions).</summary>
    private static ViewRequest Sidecar(string model, string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(model, ".json")));
        foreach (var view in json.RootElement.GetProperty("views").EnumerateArray())
        {
            if (view.GetProperty("name").GetString() != name) continue;
            double[] V(string key) => view.GetProperty("plane").GetProperty(key).EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var o = V("origin"); var x = V("x"); var y = V("y");
            return ViewRequest.Parse(new Plane(new Point3d(o[0], o[1], o[2]), new Vector3d(x[0], x[1], x[2]), new Vector3d(y[0], y[1], y[2]))) with { Name = name };
        }
        throw new ArgumentException("No sidecar view " + name);
    }

    /// <summary>Canonical, order-independent export. Coordinates are written round-trip exact.</summary>
    private static object Dump(ViewDrawing drawing, double tolerance)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static double[] Flat(IEnumerable<Point3d> points) => points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        double[] Points(Curve curve)
        {
            if (curve.TryGetPolyline(out var exact)) return Flat(exact);
            using var approximation = curve.ToPolyline(tolerance / 8, .02, 0, 0);
            return approximation is not null && approximation.TryGetPolyline(out var polyline) ? Flat(polyline) : Flat(new[] { curve.PointAtStart, curve.PointAtEnd });
        }
        var pieces = new List<(string Key, object Value)>();
        foreach (var item in drawing.Items)
        {
            using var geometry = item.CopyGeometry();
            var points = geometry is Curve curve ? Points(curve) : geometry is Point point ? Flat(new[] { point.Location }) : Array.Empty<double>();
            var key = $"{item.Class}|{item.SourceIdentity}|{item.Profile}|{string.Join(",", points.Select(N))}";
            pieces.Add((key, new { c = item.Class.ToString(), s = item.SourceIdentity, profile = item.Profile, exact = geometry is Curve c2 && c2.TryGetPolyline(out _), p = points,
                length = geometry is Curve measured ? measured.GetLength() : 0, solved = item.SolvedClass.ToString(), key = item.GeometricKey }));
        }
        var regions = new List<(string Key, object Value)>();
        foreach (var entry in drawing.Regions)
        {
            var region = entry.Region;
            var loops = region.Loops.Select(l => new { hole = l.IsHole, length = l.Curve.GetLength(), origins = l.Edges.Select(e => (int)e.Origin).ToArray(), p = Points(l.Curve) }).ToArray();
            var key = $"{region.Id}|{string.Join(";", region.SourceIds)}|{N(region.Area)}|{string.Join("/", loops.Select(l => string.Join(",", l.p.Select(N))))}";
            regions.Add((key, new { id = region.Id, sources = region.SourceIds, status = region.Status.ToString(), area = region.Area, hatch = region.Hatch is not null, loops }));
        }
        return new
        {
            error = drawing.Error, tolerance, quarantined = drawing.Report.Quarantined, uncertain = drawing.Report.UncertainCount, invalid = drawing.Report.InvalidExtractionCount,
            hiddenPoints = drawing.HiddenPoints, duplicates = drawing.DuplicateSegments, projecting = drawing.ProjectingSegments,
            diagnostics = drawing.Diagnostics.Order(StringComparer.Ordinal).ToArray(),
            pieces = pieces.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
            regions = regions.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value).ToArray()
        };
    }
}
