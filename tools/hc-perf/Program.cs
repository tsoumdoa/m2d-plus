using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Visibility;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

namespace M2DPlus.HcPerf;

/// <summary>
/// PERF hill-climb bench. One Rhino.Inside process, serial. For each model and view it runs the configured engine
/// configurations back to back on the same prepared scene, times them, and compares the drawing loci with plain native HLD.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var resolver = Assembly.LoadFrom(args[0]);
        resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
            .Invoke(null, new object[] { @"C:\Program Files\Rhino 8\System" });
        return Run(args);
    }

    /// <param name="Reuse">Load the native reference instead of solving (only when its time is at least ReuseOver seconds).</param>
    /// <param name="Save">Write the native reference (iteration 0 only).</param>
    private sealed record Config(string Name, RasterOptions? Raster, bool Reuse, double ReuseOver, bool Save, double MaxNative);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        if (args[1] == "probe") { Probe.Run(args[2], args[3]); return 0; }
        if (args[1] == "edgeproof") { Probe.EdgeProof(args[2], args[3], args[4], double.Parse(args[5]), double.Parse(args[6])); return 0; }
        if (args[1] == "classify") { Probe.Classify(args[2], args[3], args.Length > 4 ? args[4] : null); return 0; }
        var spec = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
        var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        var refDir = Path.GetFullPath(spec["refDir"]!.GetValue<string>()); Directory.CreateDirectory(refDir);
        var configs = spec["configs"]!.AsArray().Select(c => new Config(c!["name"]!.GetValue<string>(),
            c["raster"] is JsonObject o ? o.Deserialize<RasterOptions>() : null, c["reuse"]?.GetValue<bool>() ?? false,
            c["reuseOver"]?.GetValue<double>() ?? 0, c["save"]?.GetValue<bool>() ?? !(c["reuse"]?.GetValue<bool>() ?? false),
            c["maxNative"]?.GetValue<double>() ?? double.PositiveInfinity)).ToList();
        var metaConfigs = spec["metamorphic"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() ?? new();
        var repThreshold = spec["repThreshold"]?.GetValue<double>() ?? 10; var reps = spec["reps"]?.GetValue<int>() ?? 3;
        var only = args.Length > 3 ? args[3] : null;
        using var log = new StreamWriter(Path.Combine(output, "results.jsonl"), append: true) { AutoFlush = true };
        foreach (var item in spec["items"]!.AsArray())
        {
            var model = item!["model"]!.GetValue<string>();
            if (only is not null && !model.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var group = item["group"]?.GetValue<string>() ?? "";
            try { RunModel(model, group, item.AsObject(), configs, metaConfigs, refDir, reps, repThreshold, log); }
            catch (Exception e) { log.WriteLine(JsonSerializer.Serialize(new { model, group, error = e.ToString() })); Console.WriteLine($"{model}: {e.Message}"); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        return 0;
    }

    private static void RunModel(string model, string group, JsonObject item, List<Config> configs, HashSet<string> metaConfigs,
        string refDir, int reps, double repThreshold, StreamWriter log)
    {
        var load = Stopwatch.StartNew();
        using var document = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(1L << 40);
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromHours(1))) { }
        using var lease = capture.Accept();
        var scene = lease.Value; var sources = scene.Prepare(CancellationToken.None);
        var loadSeconds = load.Elapsed.TotalSeconds;
        var modelName = Path.GetFileNameWithoutExtension(model);
        var extent = scene.Bounds.IsValid ? scene.Bounds.Diagonal.Length : 1;
        Console.WriteLine($"{modelName}: {sources.Count} sources, load {loadSeconds:F1}s");
        foreach (var viewNode in item["views"]!.AsArray())
        {
            var viewName = viewNode!["name"]!.GetValue<string>();
            var label = viewNode["label"]?.GetValue<string>() ?? viewName;
            var gate = viewNode["gate"]?.GetValue<bool>() ?? false;
            var timed = viewNode["timed"]?.GetValue<bool>() ?? true;
            var view = ViewRequest.Parse(viewName, document);
            if (viewNode["tile"] is JsonArray tile && view.Named is { } named)
                view = view with { Named = named with { X = new Interval(tile[0]!.GetValue<double>(), tile[1]!.GetValue<double>()), Y = new Interval(tile[2]!.GetValue<double>(), tile[3]!.GetValue<double>()) } };
            var tolerance = scene.Tolerance; var tau = Math.Max(10 * tolerance, 1e-6 * extent);
            var key = Sanitize(modelName + "__" + label);
            Locus? reference = null; double? nativeSeconds = null;
            foreach (var config in configs)
            {
                var refFile = Path.Combine(refDir, key + ".loci"); var refMeta = Path.Combine(refDir, key + ".json");
                if (config.Raster is null && config.Reuse && File.Exists(refFile) && File.Exists(refMeta))
                {
                    var stored = JsonNode.Parse(File.ReadAllText(refMeta))!["median"]!.GetValue<double>();
                    if (stored >= config.ReuseOver)
                    {
                        reference = Locus.Load(refFile); nativeSeconds = stored;
                        continue;
                    }
                }
                // Skip this configuration on views whose native reference is slower than maxNative (timings reused from an earlier pass).
                if (config.Raster is not null && File.Exists(refMeta) && JsonNode.Parse(File.ReadAllText(refMeta))!["median"]!.GetValue<double>() >= config.MaxNative) continue;
                var settings = new NativeSettings { Raster = config.Raster };
                var times = new List<double>(); long peak = 0; Locus? locus = null; ViewDrawing? last = null;
                for (var rep = 0; rep < reps; rep++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    using var sampler = new MemorySampler();
                    var timer = Stopwatch.StartNew();
                    var drawing = NativeDrawing.SolveView(scene, sources, view, settings, CancellationToken.None);
                    times.Add(timer.Elapsed.TotalSeconds); peak = Math.Max(peak, sampler.Stop());
                    last?.Dispose(); last = drawing;
                    if (timer.Elapsed.TotalSeconds >= repThreshold || !timed) break;
                }
                using (last)
                {
                    locus = Extract(last!, tolerance, tau, extent);
                    var median = Median(times);
                    var record = new Dictionary<string, object?>
                    {
                        ["model"] = modelName, ["group"] = group, ["view"] = label, ["config"] = config.Name, ["timed"] = timed, ["gate"] = gate,
                        ["seconds"] = times, ["median"] = median, ["peakBytes"] = peak, ["sources"] = sources.Count, ["loadSeconds"] = loadSeconds,
                        ["error"] = last!.Error, ["pieces"] = last.Items.Count, ["visibleLength"] = locus.Length(Locus.Visible), ["hiddenLength"] = locus.Length(Locus.Hidden),
                        ["unresolvedLength"] = locus.Length(Locus.Unresolved), ["hiddenPoints"] = last.HiddenPoints, ["prepass"] = last.Prepass, ["tau"] = tau,
                    };
                    if (config.Raster is null)
                    {
                        reference = locus; nativeSeconds = median;
                        if (metaConfigs.Contains(config.Name) && (item["metamorphic"]?.GetValue<bool>() ?? false))
                            record["metamorphic"] = Metamorphic(scene, sources, view, settings, tolerance, tau, extent);
                        if (config.Save) { locus.Save(refFile); File.WriteAllText(refMeta, JsonSerializer.Serialize(record)); }
                    }
                    else
                    {
                        record["nativeSeconds"] = nativeSeconds;
                        var directLength = 0.0;
                        if (last.Prepass is not null)
                        {
                            var removed = last.RemovedSources.ToHashSet(StringComparer.Ordinal);
                            directLength = locus.Pieces.Where(p => p.Class == Locus.Hidden && removed.Contains(locus.Sources[p.Source])).Sum(p => Locus.PolyLength(p.Points));
                            if (reference is not null)
                            {
                                var culledVisible = reference.Pieces.Where(p => p.Class == Locus.Visible && removed.Contains(reference.Sources[p.Source]))
                                    .Select(p => (Id: reference.Sources[p.Source], Length: p.Points.Length == 2 ? 0 : Locus.PolyLength(p.Points))).ToList();
                                record["culledWithVisible"] = culledVisible.GroupBy(c => c.Id).Select(g => new { id = g.Key, length = g.Sum(c => c.Length), pieces = g.Count() }).Take(50).ToList();
                                record["culledWithVisibleCount"] = culledVisible.Select(c => c.Id).Distinct().Count();
                            }
                        }
                        record["certifiedLength"] = directLength;
                        if (System.Environment.GetEnvironmentVariable("HCPERF_DUMP") == "1") locus.Save(Path.Combine(Path.GetDirectoryName(refDir)!, key + "." + config.Name + ".loci"));
                        if (reference is not null)
                        {
                            var directSet = last.DirectSources;
                            var removedSet = last.RemovedSources.ToHashSet(StringComparer.Ordinal);
                            var cmp = LocusCompare.Compare(locus, reference, tau, extent, last.Prepass is null ? null : id => directSet.Contains(id), id => removedSet.Contains(id));
                            record["compare"] = new { cmp.Unmatched, cmp.Lengths, cmp.SignificantSpans, cmp.SignificantLength, cmp.PointMismatches, cmp.UnresolvedSwapLength, cmp.NativeSolvedSpans, cmp.NativeSolvedLength, cmp.ExtraCulledHiddenLength, spans = cmp.Significant.Concat(cmp.Spans.Where(x => x.Kind == "unresolved-swap").Take(4)).ToList() };
                        }
                        if (metaConfigs.Contains(config.Name) && (item["metamorphic"]?.GetValue<bool>() ?? false))
                            record["metamorphic"] = Metamorphic(scene, sources, view, settings, tolerance, tau, extent);
                    }
                    log.WriteLine(JsonSerializer.Serialize(record));
                    Console.WriteLine($"  {label} [{config.Name}] {median:F3}s pieces={last.Items.Count} {(last.Prepass is { } p ? $"culled={p.Culled}/{p.Sources} outside={p.Outside}" : "")} {last.Error}");
                }
            }
        }
    }

    private static object Metamorphic(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, double tolerance, double tau, double extent)
    {
        var frame = view.Resolve(scene.Bounds, tolerance);
        ViewRequest Framed(Transform t)
        {
            var plane = frame.Plane; plane.Transform(t); var direction = view.Direction; direction.Transform(t);
            return new ViewRequest(view.Name, direction) { Kind = ViewSourceKind.NamedView, Named = new NamedFrame(plane, frame.X, frame.Y, view.Named?.Crop ?? false) };
        }
        var direct = new HashSet<string>(StringComparer.Ordinal); var removed = new HashSet<string>(StringComparer.Ordinal);
        Func<string, bool>? Attribution() => settings.Raster is null ? null : id => direct.Contains(id);
        Func<string, bool>? Culled() => settings.Raster is null ? null : id => removed.Contains(id);
        Locus Solve(IReadOnlyList<PreparedLeaf> input, ViewRequest v)
        {
            using var d = NativeDrawing.SolveView(scene, input, v, settings, CancellationToken.None);
            direct.UnionWith(d.DirectSources); removed.UnionWith(d.RemovedSources); return Extract(d, tolerance, tau, extent);
        }
        var baseline = Solve(sources, Framed(Transform.Identity));
        var results = new Dictionary<string, object>();
        var random = new Random(20261114); var permuted = sources.OrderBy(_ => random.Next()).ToList();
        var permutedLocus = Solve(permuted, Framed(Transform.Identity));
        var cmp = LocusCompare.Compare(permutedLocus, baseline, tau, extent, Attribution(), Culled(), bothSidesOurs: true);
        results["permutation"] = new { pass = cmp.SignificantSpans == 0 && cmp.PointMismatches == 0, cmp.SignificantSpans, cmp.SignificantLength, cmp.PointMismatches, cmp.UnresolvedSwapLength, cmp.NativeSolvedLength, spans = cmp.Significant.Take(5) };
        var center = scene.Bounds.Center;
        foreach (var (name, t) in new[] { ("rotateZ180", Transform.Rotation(Math.PI, Vector3d.ZAxis, center)), ("rotateX180", Transform.Rotation(Math.PI, Vector3d.XAxis, center)) })
        {
            var moved = sources.Select(s => { var g = s.Geometry.Duplicate(); g.Transform(t); return s with { Geometry = g }; }).ToList();
            try
            {
                var movedLocus = Solve(moved, Framed(t));
                var c = LocusCompare.Compare(movedLocus, baseline, tau, extent, Attribution(), Culled(), bothSidesOurs: true);
                results[name] = new { pass = c.SignificantSpans == 0 && c.PointMismatches == 0, c.SignificantSpans, c.SignificantLength, c.PointMismatches, c.UnresolvedSwapLength, c.NativeSolvedLength, spans = c.Significant.Take(5) };
            }
            finally { foreach (var m in moved) m.Geometry.Dispose(); }
        }
        return results;
    }

    internal static Locus Extract(ViewDrawing drawing, double tolerance, double tau, double extent)
    {
        var locus = new Locus(); var plane = drawing.Frame.Plane;
        foreach (var item in drawing.Items)
        {
            var cls = item.Class switch { LineClass.Visible => Locus.Visible, LineClass.Hidden => Locus.Hidden, LineClass.Unresolved => Locus.Unresolved, _ => Locus.Other };
            using var geometry = item.CopyGeometry();
            double[]? points = null;
            if (geometry is global::Rhino.Geometry.Point point) { plane.ClosestParameter(point.Location, out var u, out var v); points = new[] { u, v }; }
            else if (geometry is Curve curve)
            {
                Point3d[] pts;
                if (curve.TryGetPolyline(out var polyline)) pts = polyline.ToArray();
                else if (curve.IsLinear(tolerance)) pts = new[] { curve.PointAtStart, curve.PointAtEnd };
                else
                {
                    // A maximum edge length keeps cusps (edge-on arcs fold back in projection) within tau.
                    using var pl = curve.ToPolyline(tau / 4, .02, 0, Math.Max(8 * tau, extent / 4000));
                    pts = pl is not null && pl.TryGetPolyline(out var p2) ? p2.ToArray() : new[] { curve.PointAtStart, curve.PointAtEnd };
                }
                points = new double[pts.Length * 2];
                for (var k = 0; k < pts.Length; k++) { plane.ClosestParameter(pts[k], out var u, out var v); points[2 * k] = u; points[2 * k + 1] = v; }
            }
            if (points is not null) locus.Pieces.Add(new(cls, locus.Source(item.SourceIdentity), points));
        }
        return locus;
    }

    private static double Median(List<double> values) { var s = values.Order().ToArray(); return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2; }
    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private sealed class MemorySampler : IDisposable
    {
        private readonly Thread _thread; private volatile bool _stop; private long _peak; private readonly long _start;
        internal MemorySampler()
        {
            var process = Process.GetCurrentProcess(); _start = process.PrivateMemorySize64; _peak = _start;
            _thread = new Thread(() => { while (!_stop) { process.Refresh(); _peak = Math.Max(_peak, process.PrivateMemorySize64); Thread.Sleep(20); } }) { IsBackground = true };
            _thread.Start();
        }
        internal long Stop() { _stop = true; _thread.Join(); return _peak - _start; }
        public void Dispose() { _stop = true; }
    }
}
