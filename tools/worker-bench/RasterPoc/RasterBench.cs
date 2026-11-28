using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Experimental.RasterVisibility;
using M2DPlus.Measurements;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Experimental.RasterVisibility;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;
using Environment = System.Environment;

internal static class RasterBench
{
    private const string Stop = "RASTER_STAGE0_CAPTURE_STOP";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    // raster-poc resolver label view fresh-output [1024,2048,4096] [mesh]
    internal static void Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        RasterControls.Run();
        if (args[2] == "score-archives")
        { ScoreArchives(args[3], int.Parse(args[4]), args[5], args[6]); return; }
        var label = args[2]; var viewName = args[3]; var folder = Path.GetFullPath(args[4]);
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Preserve existing evidence; choose a new output folder");
        var root = Environment.GetEnvironmentVariable("M2D_MODELS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models");
        var model = Model(root, label);
        var resolutions = (args.Length > 5 ? args[5] : "1024,2048,4096").Split(',').Select(int.Parse).ToArray();
        if (resolutions.Any(r => r < 64 || r > 16384)) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(folder);
        using var mutex = new Mutex(false, @"Local\M2DPlusLocalModelSuiteRhino");
        Console.WriteLine("Waiting for shared model-suite timing mutex");
        var wait = Stopwatch.StartNew();
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        var admissionSeconds = wait.Elapsed.TotalSeconds;
        var timingReference = args.FirstOrDefault(a => a.StartsWith("--timing-only=", StringComparison.Ordinal))?[14..];
        var reuseReference = args.FirstOrDefault(a => a.StartsWith("--reference=", StringComparison.Ordinal))?[12..];
        var excludeLayerFile = args.FirstOrDefault(a => a.StartsWith("--exclude-layer-file=", StringComparison.Ordinal))?[21..];
        var viewRequest = args.FirstOrDefault(a => a.StartsWith("--view-request=", StringComparison.Ordinal))?[15..];
        try { Measure(model, label, viewName, folder, resolutions, args.Contains("mesh"), admissionSeconds, timingReference, reuseReference, viewRequest, args.Contains("--also-threaded"), excludeLayerFile, args.Contains("--product-only"), args.Contains("--native-timing"), args.Contains("--reservation-only")); }
        catch (Exception ex) { Write(Path.Combine(folder, "failure.json"), new { Label = label, View = viewName, Error = ex.ToString() }); throw; }
        finally { mutex.ReleaseMutex(); }
    }
    internal static string Model(string root, string label)
    {
        if (label is "owner-1" or "owner-2")
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "owner", "manifest.json")));
            return Path.Combine(root, "owner", manifest.RootElement.GetProperty("models").GetProperty(label).GetProperty("file").GetString()!);
        }
        return label switch
        {
            "perf-M" => Path.Combine(root, "buildings", "perf-M-blocks.3dm"),
            "curved-S" => Path.Combine(root, "curved", "curved-perf-S-blocks.3dm"),
            "brep_box" or "brep_curved_sphere" or "brep_trimmed_planar_annulus" or "mesh_closed_tri" or "mesh_closed_quad" or "mesh_open_quad" or "chs_hollow" or "column_through_slab" or "block_nested_mirrored" or "subd_rounded_cube"
                => Path.GetFullPath(Path.Combine("tests", "fixtures", label + ".3dm")),
            _ => throw new NotSupportedException("Use owner labels, perf-M, curved-S or a listed small fixture")
        };
    }
    internal static ViewRequest ReadView(RhinoDoc doc, string name, string? descriptor = null)
    {
        if (descriptor is not null)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(descriptor))!;
            var node = root["View"] ?? root;
            node["Settings"] = null; // Read authored geometry/depth; resolve current settings explicitly below.
            return WorkerCodec.Decode<ViewRequest>(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())) with { Name = name };
        }
        var view = ViewRequest.Parse(name, doc);
        if (view.Error is null) return view;
        var objects = doc.Objects.GetObjectList(ObjectType.AnyObject).Where(o => string.Equals(o.Attributes.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return objects.Length == 1 ? ViewRequest.Parse(objects[0], doc) with { Name = name } : view;
    }
    private static void Measure(string model, string label, string viewName, string folder, int[] resolutions, bool mesh, double admissionSeconds, string? timingReference, string? reuseReference, string? viewDescriptor, bool alsoThreaded, string? excludeLayerFile, bool productOnly, bool nativeTiming, bool reservationOnly)
    {
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model) ?? throw new IOException("Cannot open model label " + label);
        var scale = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
        var units = doc.ModelUnitSystem switch
        { UnitSystem.Millimeters => DocumentUnits.Millimetres, UnitSystem.Meters => DocumentUnits.Metres, UnitSystem.Feet => DocumentUnits.Feet, _ => throw new NotSupportedException("Benchmark document units") };
        var view = ReadView(doc, viewName, viewDescriptor);
        if (view.Error is not null) throw new InvalidOperationException(view.Error);
        if (label == "owner-2" && viewName == "Plan A" && view.Cut is null)
            throw new InvalidDataException("The requested owner Plan A must resolve as an authored cut");
        var authoredDepth = view.Cut?.SourceDepth;
        var cheap = BoundingBox.Empty;
        foreach (var obj in doc.Objects.GetObjectList(ObjectType.AnyObject))
            if (SceneCapture.IsDrawable(doc, obj)) cheap.Union(obj.Geometry.GetBoundingBox(false));
        var settingsText = "hidden=on\ntangents=off\nseams=off\nbeyond=off\nmin visible=0\nfarclip=noline";
        if (authoredDepth is double depth) settingsText += "\ndepth=" + depth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var resolved = Settings.Resolve([new(viewName, viewName, view.Kind, view.Cut is not null, cheap.Diagonal.Length,
            doc.ModelAbsoluteTolerance, authoredDepth)], [[settingsText]], units);
        if (resolved.HasErrors) throw new InvalidOperationException(string.Join("; ", resolved.Diagnostics));
        view = view with { Settings = resolved.Views[0] };
        if (authoredDepth is double expectedDepth && Math.Abs(view.Settings.Depth - expectedDepth) > doc.ModelAbsoluteTolerance * .01)
            throw new InvalidDataException("Authored SourceDepth was lost during settings resolution");
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, null, cache,
            view.ObjectId == Guid.Empty ? null : new HashSet<Guid> { view.ObjectId }, null, _ => [view]);
        while (!capture.Step(TimeSpan.FromSeconds(30))) { }
        using var sceneLease = capture.Accept(); var scene = sceneLease.Value;
        if (reservationOnly)
        {
            Write(Path.Combine(folder, "reservation.json"), new { Label = label, View = viewName, scene.Cost,
                OldReservation = checked(128 * RasterPolicy.MiB + (scene.Cost.MeshTriangles + scene.Cost.BrepFaces * 512) * 320 + scene.Cost.Sources * 4096L),
                NewReservation = RasterPolicy.Reservation(scene.Cost), ProjectionEstimate = RasterPolicy.ProjectionEstimate(scene.Cost), WorkerExtra = RasterPolicy.WorkerExtra(scene.Cost),
                HldWorkerCap = WorkerMemoryPolicy.Required(scene.Cost) });
            return;
        }
        var registered = new List<VisibilityInput>(); var before = NativeDrawing.HldCalls;
        var scheduler = new NativeScheduler(WorkerMemoryPolicy.Budget(WorkerMachineMemory.Read()));
        var clock = Stopwatch.StartNew();
        using var stopped = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, "raster-stage0", viewName, 1), [view],
            new() { IsWorker = true, Scheduler = scheduler, MultipleThreads = false, ObserveNativeInputs = inputs =>
            {
                if (registered.Count > 0) throw new InvalidDataException("More than one HLD registration batch");
                foreach (var input in inputs)
                {
                    var owned = input.Geometry.Duplicate(); owned.EnsurePrivateCopy();
                    registered.Add(input with { Geometry = owned });
                }
                throw new InvalidOperationException(Stop);
            } }, default, null).GetAwaiter().GetResult();
        var preparationSeconds = clock.Elapsed.TotalSeconds;
        try
        {
            var captureResult = stopped.Views.Single();
            if (NativeDrawing.HldCalls != before || registered.Count == 0 || captureResult.Error != Stop)
                throw new InvalidDataException("Interception must capture a nonempty batch with zero HLD calls: " + captureResult.Error);
            if (registered.Select(i => i.Source.Identity).Distinct().Count() > scene.SourceCount)
                throw new InvalidDataException("Registration source census exceeds captured scene");
            var resolvedView = ViewSourceCull.ResolveView(view, scene.Bounds, scene.Tolerance);
            if (authoredDepth is double authored && resolvedView.Cut?.SourceDepth != authored)
                throw new InvalidDataException("Resolved route dropped authored depth");
            var frame = captureResult.Frame;
            var tolerance = view.Settings.Tolerance;
            var inputDigest = WorkerCodec.Hash(WorkerCodec.Encode(registered.Select(i => new
                { hash = GeometryFingerprint.Compute(i.Geometry), i.Source.Identity, i.Far }).ToArray()));
            var sourceCount = registered.Select(i => i.Source.Identity).Distinct().Count();
            var preparedCount = scene.Prepare(default).Count;
            if (preparedCount != scene.SourceCount || sourceCount > preparedCount || registered.Any(i => !scene.SourceMetadata.ContainsKey(i.Source.Identity)))
                throw new InvalidDataException("Prepared/captured/registered source census mismatch");
            if (resolvedView.Cut is null && view.Named is not { Crop: true } && sourceCount != preparedCount)
                throw new InvalidDataException("Unbounded uncut view lost a prepared source");
            if (nativeTiming)
            {
                using var memory = new MemorySample();
                using var result = RasterProjection.Solve(registered, resolvedView, frame, tolerance, doc.ModelAngleToleranceRadians,
                    resolutions.Single(), Math.Min(16, Environment.ProcessorCount), parallelNative: false);
                memory.Stop();
                Write(Path.Combine(folder, "native-timing.json"), new { Label = label, View = viewName, scene.Cost,
                    result.Timings, result.Drawing.RasterEstimatedBytes, PeakPrivateBytes = memory.PeakPrivate,
                    NativeParallel = false, ManagedThreads = Math.Min(16, Environment.ProcessorCount), HldCallDelta = NativeDrawing.HldCalls - before });
                Console.WriteLine($"Serial native Raster: {result.Timings.Total:F3}s; mesh={result.Timings.Tessellate:F3}s; edges={result.Timings.Edges:F3}s; silhouettes={result.Timings.Silhouette:F3}s");
                return;
            }
            using var reference = timingReference is not null || reuseReference is not null
                ? JsonDocument.Parse(File.ReadAllBytes(timingReference ?? reuseReference!)) : null;
            if (reference is not null)
            {
                var expected = reference.RootElement;
                if (expected.GetProperty("InputDigest").GetString() != inputDigest || expected.GetProperty("SceneSources").GetInt32() != scene.SourceCount ||
                    expected.GetProperty("Label").GetString() != label || expected.GetProperty("View").GetString() != viewName ||
                    expected.GetProperty("Settings").GetString() != settingsText ||
                    expected.GetProperty("ModelHash").GetString() != WorkerCodec.Hash(File.ReadAllBytes(model)) ||
                    expected.GetProperty("HldMultipleThreads").GetBoolean() ||
                    !WorkerCodec.Fingerprint().Accepts(expected.GetProperty("Fingerprint").Deserialize<WorkerFingerprint>(Json)!))
                    throw new InvalidDataException("Inputs differ from the single-threaded reference");
            }
            if (productOnly)
            {
                var rasterSettings = Settings.Resolve([new(viewName, viewName, view.Kind, view.Cut is not null, cheap.Diagonal.Length,
                    doc.ModelAbsoluteTolerance, authoredDepth)], [[settingsText + "\nvisibility=raster"]], units).Views[0];
                var productView = view with { Settings = rasterSettings };
                var plan = WorkerMemoryPolicy.Size(scene.Cost, 1, 1, WorkerMachineMemory.Read(), extra: RasterPolicy.WorkerExtra(scene.Cost));
                if (plan.Refusal is not null) throw new InvalidOperationException(plan.Refusal);
                var calls = NativeDrawing.HldCalls;
                using var product = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, "raster-product-smoke", viewName, 1), [productView],
                    new() { IsWorker = true, Scheduler = new(plan.CapBytes), ObserveHldOptions = _ => throw new InvalidOperationException("HLD forbidden in Raster product smoke") },
                    default, null).GetAwaiter().GetResult();
                var drawing = product.Views.Single();
                Write(Path.Combine(folder, "product-smoke.json"), new { Label = label, View = viewName, Fingerprint = WorkerCodec.Fingerprint(),
                    InputDigest = inputDigest, scene.Cost, plan.CapBytes, ExtraReservation = RasterPolicy.Reservation(scene.Cost),
                    drawing.RasterRan, drawing.RasterPixel, drawing.RasterTriangles, drawing.RasterCandidates, drawing.RasterEstimatedBytes,
                    drawing.NativeCalls, HldCallDelta = NativeDrawing.HldCalls - calls, drawing.Error, drawing.Info, drawing.Seconds,
                    Pieces = drawing.Items.Count, Regions = drawing.Regions.Count });
                if (drawing.Error is not null || !drawing.RasterRan || drawing.NativeCalls != 0 || NativeDrawing.HldCalls != calls)
                    throw new InvalidDataException("Product Raster smoke failed: " + drawing.Info);
                Console.WriteLine($"Product Raster smoke passed: {drawing.Seconds:F3}s; {drawing.Items.Count} pieces; {drawing.Regions.Count} regions; cap={plan.CapBytes}; pixel={drawing.RasterPixel:F4}mm");
                return;
            }
            var invalidBeforeReference = registered.Count(i => !i.Geometry.IsValid);
            if (alsoThreaded)
            {
                using var production = new ViewDrawing { Name = viewName, Frame = frame, Settings = view.Settings, Tolerance = tolerance };
                using var productionMemory = new MemorySample();
                Console.WriteLine($"START {label}/{viewName}: production-threaded HLD timing on the same captured registrations");
                HldProjection.Solve(registered, production, resolvedView, new() { MultipleThreads = true }, scene.Bounds, default);
                productionMemory.Stop();
                if (production.RoutingQuarantine || production.NativeCalls != 1 || production.FastNativeSources != sourceCount ||
                    production.Diagnostics.Any(d => d.StartsWith(HldProjection.SkippedSource, StringComparison.Ordinal)))
                    throw new InvalidDataException("Threaded timing solve failed or skipped registrations");
                Write(Path.Combine(folder, "threaded-timing.json"), new { Label = label, View = viewName, HldMultipleThreads = true,
                    HldPhases = production.Phases.Seconds, InputDigest = inputDigest, Fingerprint = WorkerCodec.Fingerprint(),
                    PeakPrivateBytes = productionMemory.PeakPrivate, PeakWorkingSetBytes = productionMemory.PeakWorking });
                Console.WriteLine($"  Production-threaded HLD Compute: {production.Phases.Seconds["native solve"]:F3}s");
            }
            using var hld = reuseReference is null ? new ViewDrawing { Name = viewName, Frame = frame, Settings = view.Settings, Tolerance = tolerance }
                : WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reuseReference))!, "hld.json")));
            if (reuseReference is not null && (!hld.Frame.Equals(frame) || hld.Tolerance != tolerance))
                throw new InvalidDataException("Reference frame/tolerance mismatch");
            using (var memory = new MemorySample())
            {
                Console.WriteLine($"START {label}/{viewName}: scene={scene.SourceCount}, registrations={registered.Count}, depth={resolvedView.Cut?.SourceDepth}; HLD threads={timingReference is not null}");
                if (reuseReference is null) HldProjection.Solve(registered, hld, resolvedView, new() { MultipleThreads = timingReference is not null }, scene.Bounds, default);
                memory.Stop();
                if (hld.RoutingQuarantine || hld.NativeCalls != 1 || hld.FastNativeSources != sourceCount ||
                    hld.Diagnostics.Any(d => d.StartsWith(HldProjection.SkippedSource, StringComparison.Ordinal)))
                    throw new InvalidDataException("HLD reference failed or did not accept the complete source census");
                File.WriteAllBytes(Path.Combine(folder, "hld.json"), WorkerCodec.CaptureDrawing(hld));
                var header = new
                {
                    Schema = 1, Label = label, View = viewName, Utc = DateTime.UtcNow,
                    ModelHash = WorkerCodec.Hash(File.ReadAllBytes(model)), Fingerprint = WorkerCodec.Fingerprint(),
                    BenchHash = WorkerCodec.Hash(File.ReadAllBytes(typeof(RasterBench).Assembly.Location)),
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    HldMultipleThreads = timingReference is not null, ThreadedTimingReference = timingReference,
                    ThreadedTimingBeforeReference = alsoThreaded,
                    ReferenceReusedFrom = reuseReference,
                    Threads = Math.Min(16, Environment.ProcessorCount), AdmissionSeconds = admissionSeconds,
                    Settings = settingsText, AuthoredSourceDepth = authoredDepth, ResolvedDepth = resolvedView.Cut?.SourceDepth,
                    AuthoredViewArchive = System.Text.Encoding.UTF8.GetString(WorkerCodec.Encode(view with { Settings = null })),
                    ViewDescriptor = viewDescriptor, ViewDescriptorHash = viewDescriptor is null ? null : WorkerCodec.Hash(File.ReadAllBytes(viewDescriptor)),
                    SceneSources = scene.SourceCount, ScopedOut = capture.ScopedOutCount, Registrations = registered.Count,
                    RegisteredSources = registered.Select(i => i.Source.Identity).Distinct().Count(), InputDigest = inputDigest,
                    GeometryTypes = registered.GroupBy(i => i.Geometry.GetType().Name).ToDictionary(g => g.Key, g => g.Count()),
                    InvalidBeforeReference = invalidBeforeReference, InvalidAfterReference = registered.Count(i => !i.Geometry.IsValid),
                    PreparationSeconds = preparationSeconds, PreparationDiagnostics = captureResult.Diagnostics,
                    HldPhases = hld.Phases.Seconds, HldPieces = hld.Items.Count, HldDiagnostics = hld.Diagnostics,
                    HldPeakPrivateBytes = reuseReference is null ? memory.PeakPrivate : reference!.RootElement.GetProperty("HldPeakPrivateBytes").GetInt64(),
                    HldPeakWorkingSetBytes = reuseReference is null ? memory.PeakWorking : reference!.RootElement.GetProperty("HldPeakWorkingSetBytes").GetInt64(),
                    ModelUnits = doc.ModelUnitSystem.ToString(), MillimetresPerUnit = scale,
                    Comparison = "raw projection of identical prepared HLD registrations; Route C post-extraction suppression/presentation excluded from both; exact comparator after conversion to mm",
                    DrawingComparison.SpacingMm, DrawingComparison.DistanceMm
                };
                Write(Path.Combine(folder, "case.json"), header);
            }
            Console.WriteLine($"  HLD Compute: {hld.Phases.Seconds["native solve"]:F3}s; pieces={hld.Items.Count}");
            if (timingReference is not null) return; // Production-threaded timing only; accuracy always uses false.
            using var hldMm = Millimetres(hld, scale);
            var excludedLayer = excludeLayerFile is null ? null : File.ReadAllText(excludeLayerFile).Trim();
            foreach (var resolution in resolutions)
            {
                Console.WriteLine($"START raster {resolution}");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                using var memory = new MemorySample();
                var calls = NativeDrawing.HldCalls;
                using var result = RasterProjection.Solve(registered, resolvedView, frame, tolerance, doc.ModelAngleToleranceRadians,
                    resolution, Math.Min(16, Environment.ProcessorCount), mesh, parallelNative: true);
                memory.Stop();
                if (calls != NativeDrawing.HldCalls) throw new InvalidDataException("Raster called HLD");
                var sub = Path.Combine(folder, resolution.ToString()); Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(sub, "raster.json"), WorkerCodec.CaptureDrawing(result.Drawing));
                Console.WriteLine($"  raster={result.Timings.Total:F3}s; grid={result.Timings.Width}x{result.Timings.Height}; triangles={result.Timings.Triangles}; comparing");
                using var rasterMm = Millimetres(result.Drawing, scale);
                var compare = DrawingComparison.Compare(rasterMm, hldMm);
                using var rasterFiltered = WithoutLayer(rasterMm, excludedLayer);
                using var hldFiltered = WithoutLayer(hldMm, excludedLayer);
                var withoutExcludedLayer = excludedLayer is null ? null : DrawingComparison.Compare(rasterFiltered, hldFiltered);
                Write(Path.Combine(sub, "result.json"), new
                {
                    Label = label, View = viewName, Mode = mesh ? "mesh silhouettes" : "native silhouettes", result.Timings,
                    Speedup = hld.Phases.Seconds["native solve"] / result.Timings.Total,
                    PeakPrivateBytes = memory.PeakPrivate, PeakWorkingSetBytes = memory.PeakWorking,
                    compare.WrongVisibleMm, compare.WrongVisiblePercent, compare.WrongHiddenMm, compare.WrongHiddenPercent,
                    ExcludedLayerHash = excludedLayer is null ? null : WorkerCodec.Hash(System.Text.Encoding.UTF8.GetBytes(excludedLayer)),
                    WithoutExcludedLayer = withoutExcludedLayer,
                    RasterMissingMm = compare.PipesToHld.MissingMm, HldMissingMm = compare.HldToPipes.MissingMm, Compare = compare
                });
                Console.WriteLine($"  WV={compare.WrongVisibleMm:F2}mm/{compare.WrongVisiblePercent:F4}%; WH={compare.WrongHiddenMm:F2}mm/{compare.WrongHiddenPercent:F4}%");
                Errors(sub, rasterMm, hldMm, result.Candidates, scale);
            }
        }
        finally { foreach (var input in registered) input.Geometry.Dispose(); }
    }
    private static ViewDrawing Millimetres(ViewDrawing source, double factor)
    {
        var drawing = new ViewDrawing { Name = source.Name, Frame = source.Frame, Tolerance = source.Tolerance * factor };
        var transform = Transform.Scale(Point3d.Origin, factor);
        foreach (var piece in source.Items)
        {
            var geometry = piece.CopyGeometry(); geometry.Transform(transform);
            var depth = piece.DepthGeometry?.Duplicate(); depth?.Transform(transform);
            drawing.Pieces.Add(new(geometry, new PreparedLeaf(piece.SourceIdentity, piece.SourcePath, geometry) { Section = piece.Source }, piece.Class, depth));
        }
        return drawing;
    }
    private static ViewDrawing WithoutLayer(ViewDrawing source, string? excluded)
    {
        var drawing = new ViewDrawing { Name = source.Name, Frame = source.Frame, Tolerance = source.Tolerance };
        foreach (var piece in source.Items)
        {
            if (excluded is not null && piece.SourceLayer == excluded) continue;
            var geometry = piece.CopyGeometry();
            drawing.Pieces.Add(new(geometry, new PreparedLeaf(piece.SourceIdentity, piece.SourcePath, geometry) { Section = piece.Source }, piece.Class, piece.DepthGeometry?.Duplicate()));
        }
        return drawing;
    }
    // Measurement-only historical archive import. This does not relax product cache/worker restoration.
    private static void ScoreArchives(string folder, int resolution, string layerFile, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Preserve existing scoring evidence");
        using var mutex = new Mutex(false, @"Local\M2DPlusLocalModelSuiteRhino");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
            using var header = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "case.json")));
            var factor = header.RootElement.GetProperty("MillimetresPerUnit").GetDouble();
            var layer = File.ReadAllText(layerFile).Trim();
            var rasterPath = Path.Combine(folder, resolution.ToString(), "raster.json"); var hldPath = Path.Combine(folder, "hld.json");
            var current = WorkerCodec.Fingerprint();
            ViewDrawing Read(string path)
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path)); var root = json.RootElement;
                var original = root.GetProperty("Fingerprint").Deserialize<WorkerFingerprint>(WorkerCodec.Json)!;
                if (root.GetProperty("Schema").GetInt32() != WorkerCodec.Schema || original.RhinoBuild != current.RhinoBuild ||
                    original.RhinoRevision != current.RhinoRevision || original.RhinoCommonSha256 != current.RhinoCommonSha256)
                    throw new InvalidDataException("Historical scoring requires the same archive schema and exact native Rhino build");
                var drawing = new ViewDrawing { Frame = root.GetProperty("Values").GetProperty("Frame").Deserialize<Rectangle3d>(WorkerCodec.Json) };
                try
                {
                    foreach (var p in root.GetProperty("Pieces").EnumerateArray())
                    {
                        var geometry = WorkerCodec.Restore(p.GetProperty("Geometry").GetString()!);
                        var source = new PreparedLeaf(p.GetProperty("SourceIdentity").GetString()!, p.GetProperty("SourcePath").Deserialize<int[]>()!, geometry)
                            { Section = p.GetProperty("Source").Deserialize<SourceSection>(WorkerCodec.Json)! };
                        drawing.Pieces.Add(new(geometry, source, (LineClass)p.GetProperty("Class").GetInt32()));
                    }
                    return drawing;
                }
                catch { drawing.Dispose(); throw; }
            }
            var calls = NativeDrawing.HldCalls;
            using var rawRaster = Read(rasterPath); using var rawHld = Read(hldPath);
            using var raster = Millimetres(rawRaster, factor); using var hld = Millimetres(rawHld, factor);
            using var rasterFiltered = WithoutLayer(raster, layer); using var hldFiltered = WithoutLayer(hld, layer);
            var compare = DrawingComparison.Compare(rasterFiltered, hldFiltered);
            if (calls != NativeDrawing.HldCalls) throw new InvalidDataException("Archive scoring called HLD");
            Directory.CreateDirectory(output);
            Write(Path.Combine(output, "result.json"), new { Label = header.RootElement.GetProperty("Label").GetString(),
                View = header.RootElement.GetProperty("View").GetString(), HistoricalCase = Path.GetFullPath(folder),
                RasterHash = WorkerCodec.Hash(File.ReadAllBytes(rasterPath)), HldHash = WorkerCodec.Hash(File.ReadAllBytes(hldPath)),
                ExcludedLayerHash = WorkerCodec.Hash(System.Text.Encoding.UTF8.GetBytes(layer)), ScoringFingerprint = current,
                Method = "Historical geometry-only re-score with unchanged comparator; exact native Rhino/schema required; no engine run or worker/cache import", Compare = compare });
            Console.WriteLine($"Historical layer-excluded score: WV={compare.WrongVisibleMm:F2} mm; WH={compare.WrongHiddenMm:F2} mm");
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static void Errors(string folder, ViewDrawing raster, ViewDrawing hld, List<RasterProjection.Candidate> candidates, double scale)
    {
        var hidden = DrawingComparison.WrongHiddenSpans(raster, hld);
        var visible = DrawingComparison.WrongHiddenSpans(hld, raster);
        Write(Path.Combine(folder, "error-spans.json"), new { WrongVisible = visible, WrongHidden = hidden });
        var features = new List<object>();
        foreach (var (kind, drawing, spans) in new[] { ("wrong-visible", raster, visible), ("wrong-hidden", hld, hidden) })
        {
            var attribution = new Dictionary<string, double>();
            foreach (var span in spans)
            {
                var piece = drawing.Items[span.Piece]; using var curve = (Curve)piece.CopyGeometry();
                var point = curve.PointAt((span.Start + span.End) * .5); var best = double.PositiveInfinity; var feature = "unmatched / omitted candidate";
                foreach (var candidate in candidates.Where(c => c.Input.Source.Identity == piece.SourceIdentity))
                {
                    using var flat = candidate.Curve.DuplicateCurve(); flat.Transform(Transform.PlanarProjection(drawing.Frame.Plane));
                    flat.Transform(Transform.Scale(Point3d.Origin, scale));
                    if (!flat.ClosestPoint(point, out var t)) continue;
                    var distance = flat.PointAt(t).DistanceTo(point);
                    if (distance < best) { best = distance; feature = candidate.Feature; }
                }
                attribution[feature] = attribution.GetValueOrDefault(feature) + span.Mm;
            }
            features.Add(new { Kind = kind, ByFeatureMm = attribution });
            Overlay(Path.Combine(folder, kind + ".png"), drawing, spans);
        }
        Write(Path.Combine(folder, "error-features.json"), features);
    }
    private static void Overlay(string path, ViewDrawing drawing, DrawingComparison.Span[] errors)
    {
        var plane = drawing.Frame.Plane;
        var toXY = Transform.PlaneToPlane(plane, Plane.WorldXY);
        var curves = drawing.Items.Select(p => p.CopyGeometry()).OfType<Curve>().ToArray();
        try
        {
            var box = BoundingBox.Empty;
            foreach (var curve in curves) box.Union(curve.GetBoundingBox(toXY));
            if (!box.IsValid) return;
            using var bitmap = new Bitmap(1800, 1400); using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var factor = Math.Min(1740 / Math.Max(1e-9, box.Diagonal.X), 1340 / Math.Max(1e-9, box.Diagonal.Y));
            using var grey = new Pen(Color.FromArgb(110, 130, 130, 130), 1); using var red = new Pen(Color.Red, 2);
            void Draw(Curve curve, Pen pen)
            {
                var count = Math.Clamp((int)(curve.GetLength() * factor / 3), 2, 4096);
                var points = Enumerable.Range(0, count + 1).Select(i =>
                { var p = curve.PointAt(curve.Domain.ParameterAt(i / (double)count)); p.Transform(toXY); return new PointF((float)(30 + (p.X - box.Min.X) * factor), (float)(1370 - (p.Y - box.Min.Y) * factor)); }).ToArray();
                graphics.DrawLines(pen, points);
            }
            foreach (var curve in curves) Draw(curve, grey);
            foreach (var error in errors)
            {
                using var geometry = drawing.Items[error.Piece].CopyGeometry();
                if (geometry is Curve curve) { using var part = curve.Trim(error.Start, error.End); if (part is not null) Draw(part, red); }
            }
            bitmap.Save(path, ImageFormat.Png);
        }
        finally { foreach (var curve in curves) curve.Dispose(); }
    }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private sealed class MemorySample : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess(); private readonly System.Threading.Timer _timer;
        internal long PeakPrivate { get; private set; } internal long PeakWorking { get; private set; }
        internal MemorySample() { Sample(); _timer = new(_ => Sample(), null, 25, 25); }
        private void Sample() { lock (_process) { _process.Refresh(); PeakPrivate = Math.Max(PeakPrivate, _process.PrivateMemorySize64); PeakWorking = Math.Max(PeakWorking, _process.WorkingSet64); } }
        internal void Stop() { _timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); Sample(); }
        public void Dispose() { _timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _process.Dispose(); }
    }
}
