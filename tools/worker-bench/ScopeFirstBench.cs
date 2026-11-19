using System.Diagnostics;
using Environment = System.Environment;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

// The same harness compiles against main: reflection only adapts the new optional capture argument.
internal static class ScopeFirstBench
{
    internal static void Run(string model, string output, string mode)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model);
        if (mode.StartsWith("case:", StringComparison.Ordinal) && mode.Split(':').Length == 3)
        {
            var parts = mode.Split(':');
            for (var i = int.Parse(parts[1]); i < int.Parse(parts[1]) + int.Parse(parts[2]); i++)
                Measure(doc, model, Path.Combine(output, "case-" + i), "case:" + i);
        }
        else Measure(doc, model, output, mode);
    }
    private static void Measure(RhinoDoc doc, string model, string output, string mode)
    {
        if (Environment.GetEnvironmentVariable("SCOPE_BENCH_RESUME") == "1" && File.Exists(Path.Combine(output, "results.json"))) return;
        Directory.CreateDirectory(output);
        // Rhino's non-tight cached geometry bounds: no conversion, duplication or normalisation.
        var cheapClock = Stopwatch.StartNew();
        var cheap = BoundingBox.Empty;
        foreach (var obj in doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true }))
            if (SceneCapture.IsDrawable(doc, obj)) cheap.Union(obj.Geometry.GetBoundingBox(false));
        var cheapBoundsMilliseconds = cheapClock.Elapsed.TotalMilliseconds;
        var cachedBounds = cheap;
        var views = Views(doc, model, mode, cheap);
        var multipleThreads = Environment.GetEnvironmentVariable("SCOPE_BENCH_SINGLE_THREAD") != "1";
        BoundingBox? cameraBounds = Environment.GetEnvironmentVariable("SCOPE_BENCH_PIN_MAIN") is { } mainRoot
            ? ReadBounds(Path.Combine(mainRoot, Path.GetRelativePath(Environment.GetEnvironmentVariable("SCOPE_BENCH_OUTPUT_ROOT")!, output), "results.json"))
            : Environment.GetEnvironmentVariable("SCOPE_BENCH_CHEAP_RADIUS") == "1" ? cachedBounds : null;
        var rows = new List<object>();
        var repeats = int.Parse(Environment.GetEnvironmentVariable("SCOPE_BENCH_REPEATS") ?? "2");
        var warmups = int.Parse(Environment.GetEnvironmentVariable("SCOPE_BENCH_WARMUPS") ?? "1");
        for (var run = -warmups; run < repeats; run++)
        {
            using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
            var constructor = typeof(SceneCapture).GetConstructors().Single();
            object?[] arguments = [doc, watch, null, cache, null, null];
            Func<BoundingBox, IReadOnlyList<ViewRequest>> scope = _ => views;
            if (constructor.GetParameters().Length == 7) arguments = [.. arguments, scope];
            if (mode.StartsWith("warm-", StringComparison.Ordinal))
            {
                using var prior = (SceneCapture)constructor.Invoke(arguments);
                while (!prior.Step(TimeSpan.FromMilliseconds(16))) Thread.Sleep(1);
                using var priorScene = prior.Accept();
                using var priorDrawing = NativeDrawing.SolveBorrowedAsync(priorScene.Value, new(priorScene.Value.Revision, mode, "warm-prior", 1), views,
                    new() { IsWorker = true, Scheduler = new(12 * WorkerMemoryPolicy.GiB), MultipleThreads = multipleThreads, CameraBounds = cameraBounds }, default, null).GetAwaiter().GetResult();
                var outside = doc.Objects.GetObjectList(ObjectType.Extrusion).OrderByDescending(o => o.Geometry.GetBoundingBox(false).Center.Z)
                    .First();
                using var edited = outside.Geometry.Duplicate(); edited.Transform(Transform.Translation(0, .001, 0));
                if (!doc.Objects.Replace(outside.Id, (Extrusion)edited)) throw new InvalidOperationException("Warm outside edit failed.");
                watch.VerifyHostState(); // Event/snapshot observation is outside the capture timer for both states.
            }
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            using var capture = (SceneCapture)constructor.Invoke(arguments);
            while (!capture.Step(TimeSpan.FromMilliseconds(16))) Thread.Sleep(1);
            // AcceptAsync checks host coherence synchronously, then builds on owned data in the background.
            var acceptanceClock = Stopwatch.StartNew();
            var acceptance = capture.AcceptAsync();
            var acceptanceUiMilliseconds = acceptanceClock.Elapsed.TotalMilliseconds;
            using var lease = acceptance.GetAwaiter().GetResult(); var scene = lease.Value;
            var captureSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var nativeInputs = new List<string>();
            Action<IReadOnlyList<VisibilityInput>>? observe = Environment.GetEnvironmentVariable("SCOPE_BENCH_INPUTS") == "1" ? inputs =>
                nativeInputs.Add(WorkerCodec.Hash(WorkerCodec.Encode(inputs.Select(i => new { Geometry = WorkerCodec.Archive(i.Geometry), i.Source.Identity,
                    i.Source.Path, i.Source.SourceItem, i.Source.Section, i.Far }).ToArray()))) : null;
            using var batch = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, mode, "scope-bench", 1), views,
                new() { IsWorker = true, Scheduler = new(12 * WorkerMemoryPolicy.GiB), ObserveNativeInputs = observe, MultipleThreads = multipleThreads, CameraBounds = cameraBounds }, default, null).GetAwaiter().GetResult();
            if (batch.Views.Any(d => d.MultipleThreads != multipleThreads))
                throw new InvalidOperationException("Drawing threading disclosure differs from the requested Compute flag.");
            var drawingSeconds = clock.Elapsed.TotalSeconds;
            var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            process.Refresh();
            var peakCommitBytes = process.PeakPagedMemorySize64;
            var peakWorkingSetBytes = process.PeakWorkingSet64;
            if (run < 0) continue;
            var records = batch.Views.Select((drawing, i) =>
            {
                var dto = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(drawing, true));
                // Exact archive bytes and ordering: no coordinate/domain/segmentation normalization.
                var identity = WorkerCodec.Encode(new { dto.Pieces, dto.Regions, dto.Values.Frame, dto.Values.Error, dto.Values.RoutingQuarantine, dto.Values.Report.Quarantined });
                File.WriteAllBytes(Path.Combine(output, $"identity-{run}-{i}.json"), identity);
                File.WriteAllBytes(Path.Combine(output, $"drawing-{run}-{i}.json"), WorkerCodec.Encode(dto));
                return new { i, hash = WorkerCodec.Hash(identity), drawing.Error, pieces = drawing.Items.Count, regions = drawing.Regions.Count, phases = drawing.Phases.Seconds };
            }).ToArray();
            rows.Add(new { run, cpuSeconds, peakCommitBytes, peakWorkingSetBytes, captureSeconds, uiCaptureMilliseconds = capture.ActiveSeconds * 1000 + acceptanceUiMilliseconds, acceptanceUiMilliseconds,
                maximumHostCallbackMilliseconds = Math.Max(capture.InitialEnumerationMilliseconds, Math.Max(capture.MaximumSliceMilliseconds, acceptanceUiMilliseconds)), capture.MaximumSliceMilliseconds, capture.InitialEnumerationMilliseconds, capture.Slices, drawingSeconds, totalSeconds = captureSeconds + drawingSeconds, scene.SourceCount,
                nativeInputs, cheapBoundsMilliseconds, cachedBounds = new[] { cachedBounds.Min.X, cachedBounds.Min.Y, cachedBounds.Min.Z, cachedBounds.Max.X, cachedBounds.Max.Y, cachedBounds.Max.Z }, cameraBounds = cameraBounds is { } cb ? new[] { cb.Min.X, cb.Min.Y, cb.Min.Z, cb.Max.X, cb.Max.Y, cb.Max.Z } : null,
                skipped = typeof(SceneCapture).GetProperty("ScopedOutCount")?.GetValue(capture) ?? 0,
                bounds = new[] { scene.Bounds.Min.X, scene.Bounds.Min.Y, scene.Bounds.Min.Z, scene.Bounds.Max.X, scene.Bounds.Max.Y, scene.Bounds.Max.Z },
                profile = capture.Profile.Select(p => new { phase = p.Phase.ToString(), seconds = p.Milliseconds / 1000 }), records });
            File.WriteAllBytes(Path.Combine(output, "results.json"), WorkerCodec.Encode(new { model, modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)), mode,
                fingerprint = WorkerCodec.Fingerprint(), processors = Environment.ProcessorCount, workerCount = 0,
                threadedHld = multipleThreads, warmups, rows }));
            Console.WriteLine($"{Path.GetFileName(model)} / {mode} / {run}: capture {captureSeconds:F3}, drawing {drawingSeconds:F3}, sources {scene.SourceCount}");
        }
    }

    private static BoundingBox ReadBounds(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var b = json.RootElement.GetProperty("rows")[0].GetProperty("bounds").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return new(new Point3d(b[0], b[1], b[2]), new Point3d(b[3], b[4], b[5]));
    }

    internal static void RadiusCost(string model, string output)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model);
        var rows = new List<object>();
        var normalisations = SceneCapture.Normalisations;
        for (var run = 0; run < 22; run++)
        {
            var clock = Stopwatch.StartNew(); var bounds = BoundingBox.Empty; var objects = 0;
            foreach (var obj in doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true }))
                if (SceneCapture.IsDrawable(doc, obj)) { bounds.Union(obj.Geometry.GetBoundingBox(false)); objects++; }
            var milliseconds = clock.Elapsed.TotalMilliseconds;
            rows.Add(new { run, warmup = run < 2, milliseconds, objects, radius = bounds.Diagonal.Length,
                bounds = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z } });
        }
        if (normalisations != SceneCapture.Normalisations) throw new InvalidOperationException("Radius probe normalised geometry.");
        File.WriteAllBytes(Path.Combine(output, "radius-cost.json"), WorkerCodec.Encode(new { model,
            modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)), fingerprint = WorkerCodec.Fingerprint(),
            method = "Host-thread drawable enumeration + Geometry.GetBoundingBox(false); no conversions or copies", rows }));
    }

    internal static void GenerateStoreys(string model)
    {
        if (File.Exists(model)) throw new IOException("Storey model already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Meters; doc.ModelAbsoluteTolerance = .0001;
        void Solid(double x, double y, double z, double dx, double dy, double dz, bool extrude)
        {
            if (extrude)
            {
                using var profile = new Rectangle3d(new Plane(new Point3d(x, y, z), Vector3d.ZAxis), dx, dy).ToNurbsCurve();
                using var shape = Extrusion.Create(profile, dz, true); doc.Objects.AddExtrusion(shape);
            }
            else { using var shape = Brep.CreateFromBox(new BoundingBox(new Point3d(x, y, z), new Point3d(x + dx, y + dy, z + dz))); doc.Objects.AddBrep(shape); }
        }
        using var subdFile = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/subd_rounded_cube.3dm"));
        for (var floor = 0; floor < 10; floor++)
        {
            var z = floor * 4d;
            Solid(-.5, -.5, z, 25, 17, .25, false);
            for (var y = 0; y <= 4; y++) for (var x = 0; x <= 6; x++)
                Solid(x * 4 - .15, y * 4 - .15, z + .25, .3, .3, 3.75, true);
            // Four-piece facade panels surround a 2 x 1.4 m opening in each bay.
            for (var x = 0; x < 6; x++) foreach (var y in new[] { 0d, 15.8 })
            {
                Solid(x * 4, y, z + .25, .8, .2, 3.75, false); Solid(x * 4 + 2.8, y, z + .25, 1.2, .2, 3.75, false);
                Solid(x * 4 + .8, y, z + .25, 2, .2, .85, true); Solid(x * 4 + .8, y, z + 2.5, 2, .2, 1.5, true);
            }
            for (var y = 0; y < 4; y++) foreach (var x in new[] { 0d, 23.8 })
                Solid(x, y * 4, z + .25, .2, 4, 3.75, true);
            for (var y = 0; y < 4; y++) for (var x = 0; x < 6; x++)
                Solid(x * 4 + 1.2, y * 4 + 1.2, z + .25, 1.5, .8, .75, (x + y) % 2 == 0);
            foreach (var obj in subdFile.Objects.Where(o => o.Geometry is SubD))
            {
                using var copy = obj.Geometry.Duplicate(); var box = copy.GetBoundingBox(false);
                copy.Transform(Transform.Scale(box.Center, .4 / Math.Max(1, box.Diagonal.Length)));
                copy.Transform(Transform.Translation(new Point3d(6, 6, z + .8) - box.Center)); doc.Objects.Add(copy);
            }
        }
        if (!doc.WriteFile(model, new global::Rhino.FileIO.FileWriteOptions { FileVersion = 8 })) throw new IOException("Storey model write failed.");
        File.WriteAllBytes(model + ".generation.json", WorkerCodec.Encode(new { storeys = 10, height = 4, slabs = 10,
            objects = doc.Objects.Count, modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)), method = "Slabs, extruded columns and side walls; facade pieces around openings; Brep/extrusion interior objects; ten SubDs" }));
    }

    internal static void Corpus(string models, string sections, string output)
    {
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        foreach (var meta in Directory.GetFiles(Path.Combine(models, "views"), "*.json").Order())
        {
            if (Path.GetFileName(meta).Contains("curved-perf-M", StringComparison.OrdinalIgnoreCase)) continue;
            using var data = JsonDocument.Parse(File.ReadAllBytes(meta));
            if (!data.RootElement.TryGetProperty("family", out var family) || family.GetString() != "view-cases") continue;
            using var doc = RhinoDoc.OpenHeadless(Path.ChangeExtension(meta, ".3dm"));
            for (var i = 0; i < data.RootElement.GetProperty("views").GetArrayLength(); i++)
                Measure(doc, Path.ChangeExtension(meta, ".3dm"), Path.Combine(output, Path.GetFileNameWithoutExtension(meta), "case-" + i), "case:" + i);
        }
        foreach (var model in Directory.GetFiles(sections, "*.3dm").Order())
        {
            using var doc = RhinoDoc.OpenHeadless(model);
            Measure(doc, model, Path.Combine(output, "sections", Path.GetFileNameWithoutExtension(model)), "sectionbox");
        }
    }

    internal static void GenerateConversions(string model)
    {
        if (File.Exists(model)) throw new IOException("Conversion evidence already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Meters; doc.ModelAbsoluteTolerance = .0001;
        for (var y = 0; y < 50; y++) for (var x = 0; x < 50; x++)
        {
            using var curve = new Rectangle3d(new Plane(new Point3d(x, y, 0), Vector3d.ZAxis), .6, .6).ToNurbsCurve();
            using var extrusion = Extrusion.Create(curve, 1 + .1 * ((x + y) % 5), true);
            doc.Objects.AddExtrusion(extrusion);
        }
        using var file = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/subd_rounded_cube.3dm"));
        foreach (var obj in file.Objects.Where(o => o.Geometry is SubD)) for (var i = 0; i < 25; i++)
        {
            using var copy = obj.Geometry.Duplicate(); copy.Transform(Transform.Translation(i * 2, 25, 0)); doc.Objects.Add(copy);
        }
        if (!doc.WriteFile(model, new global::Rhino.FileIO.FileWriteOptions { FileVersion = 8 })) throw new IOException("Conversion model write failed.");
        File.WriteAllBytes(model + ".generation.json", WorkerCodec.Encode(new { extrusions = 2500, subds = 25, modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)) }));
    }

    internal static void GenerateTerrain(string model)
    {
        if (File.Exists(model)) throw new IOException("Terrain evidence already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Meters; doc.ModelAbsoluteTolerance = .0001;
        const int cells = 16;
        for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++)
        {
            using var mesh = new Mesh();
            for (var v = 0; v <= cells; v++) for (var u = 0; u <= cells; u++)
            {
                double px = x + u / (double)cells, py = y + v / (double)cells;
                mesh.Vertices.Add(px, py, .05 * Math.Sin(px * .7) * Math.Cos(py * .9));
            }
            for (var v = 0; v < cells; v++) for (var u = 0; u < cells; u++)
            {
                int a = v * (cells + 1) + u, b = a + 1, c = a + cells + 2, d = a + cells + 1;
                mesh.Faces.AddFace(a, b, c); mesh.Faces.AddFace(a, c, d);
            }
            mesh.Normals.ComputeNormals(); mesh.Compact(); doc.Objects.AddMesh(mesh);
        }
        if (!doc.WriteFile(model, new global::Rhino.FileIO.FileWriteOptions { FileVersion = 8 })) throw new IOException("Terrain model write failed.");
        File.WriteAllBytes(model + ".generation.json", WorkerCodec.Encode(new { tiles = 256, cells, triangles = 256 * cells * cells * 2, modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)) }));
    }

    internal static void ExportScene(string output)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.CreateHeadless(null);
        using var shape = Brep.CreateFromBox(new BoundingBox(new Point3d(-.2, -.2, -2), new Point3d(.2, .2, -1)));
        doc.Objects.AddBrep(shape); shape.Transform(Transform.Translation(8, 0, 0)); doc.Objects.AddBrep(shape);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc); using var capture = new SceneCapture(doc, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(16))) { }
        using var lease = capture.Accept(); var scene = lease.Value;
        var views = new[] { 0d, 8d }.Select(x => ViewRequest.Parse(new Rectangle3d(new Plane(new Point3d(x, 0, 0), Vector3d.ZAxis), new Interval(-1, 1), new Interval(-1, 1)))).ToArray();
        var identity = new RequestIdentity(scene.Revision, "two-views", "test", 1);
        File.WriteAllBytes(Path.Combine(output, "fingerprint.json"), WorkerCodec.Encode(WorkerCodec.Fingerprint()));
        File.WriteAllBytes(Path.Combine(output, "identity.json"), WorkerCodec.Encode(identity));
        File.WriteAllBytes(Path.Combine(output, "cost.json"), WorkerCodec.Encode(scene.Cost));
        File.WriteAllBytes(Path.Combine(output, "views.json"), WorkerCodec.Encode(views));
        using var local = NativeDrawing.SolveBorrowedAsync(scene, identity, views, new(), default, null).GetAwaiter().GetResult();
        for (var i = 0; i < views.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(output, "scene.json"), WorkerCodec.Capture(scene, default));
            File.WriteAllBytes(Path.Combine(output, $"expected-{i}.json"), Identity(local.Views[i]));
        }
    }
    internal static async Task WorkerScene(string folder, string directory)
    {
        var fingerprint = WorkerCodec.Decode<WorkerFingerprint>(File.ReadAllBytes(Path.Combine(folder, "fingerprint.json")));
        var identity = WorkerCodec.Decode<RequestIdentity>(File.ReadAllBytes(Path.Combine(folder, "identity.json")));
        var cost = WorkerCodec.Decode<SceneCost>(File.ReadAllBytes(Path.Combine(folder, "cost.json")));
        var views = WorkerCodec.Decode<ViewRequest[]>(File.ReadAllBytes(Path.Combine(folder, "views.json")));
        using var pool = new DrawingWorkerPool(new() { Count = 1, Directory = directory });
        var rows = new List<object>();
        pool.EncodedDrawing = (i, bytes) =>
        {
            var dto = WorkerCodec.Decode<WorkerCodec.Drawing>(bytes);
            var exact = WorkerCodec.Encode(new { dto.Pieces, dto.Regions, dto.Values.Frame, dto.Values.Error, dto.Values.RoutingQuarantine, dto.Values.Report.Quarantined });
            var expected = File.ReadAllBytes(Path.Combine(folder, $"expected-{i}.json"));
            File.WriteAllBytes(Path.Combine(folder, $"actual-{rows.Count}-{i}.json"), exact);
            rows.Add(new { i, equal = expected.SequenceEqual(exact), hash = WorkerCodec.Hash(exact), expected = WorkerCodec.Hash(expected) });
            if (!expected.SequenceEqual(exact)) throw new InvalidOperationException("Worker output differs from its in-process fixture.");
        };
        int[]? first = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var result = await pool.SolveEncodedAsync(cost, identity with { Generation = repeat + 1 }, views, fingerprint,
                (repeat == 0 ? File.ReadAllBytes(Path.Combine(folder, "scene.json")) : []), default);
            if (first is null) first = pool.ProcessIds;
            else if (!first.SequenceEqual(pool.ProcessIds)) throw new InvalidOperationException("Persistent worker restarted unexpectedly.");
        }
        File.WriteAllBytes(Path.Combine(folder, "worker-results.json"), WorkerCodec.Encode(new { rows, processes = pool.ProcessIds, peak = pool.PeakBytes }));
        pool.Dispose();
        await WorkerLifecycle(folder, directory, fingerprint, identity, cost, views);
    }
    private static async Task WorkerLifecycle(string folder, string directory, WorkerFingerprint fingerprint, RequestIdentity identity, SceneCost cost, ViewRequest[] views)
    {
        var scene = File.ReadAllBytes(Path.Combine(folder, "scene.json"));
        var checks = new List<string>();
        WorkerOptions Options(string? fault = null) => new() { Count = 1, Directory = directory, TestFault = fault };
        async Task<DrawingBatch> Solve(DrawingWorkerPool pool, CancellationToken token = default) =>
            await pool.SolveEncodedAsync(cost, identity, views, fingerprint, scene, token);
        void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); checks.Add(name); }
        using (var pool = new DrawingWorkerPool(Options()))
        {
            pool.EncodedDrawing = (_, _) => { };
            var killed = false;
            pool.SolvingView = (pid, _) => { if (killed) return; killed = true; using var owned = Process.GetProcessById(pid); owned.Kill(true); };
            using var failed = await Solve(pool);
            Check(failed.Views.Any(v => v.Error is not null) && failed.Views.Any(v => v.Error is null), "crash isolates one view");
            pool.SolvingView = null;
            using var recovered = await Solve(pool);
            Check(recovered.Views.All(v => v.Error is null), "crash recovery");
        }
        using (var pool = new DrawingWorkerPool(Options("block")))
        using (var cancellation = new CancellationTokenSource())
        {
            pool.SolvingView = (_, _) => cancellation.CancelAfter(100);
            try { using var result = await Solve(pool, cancellation.Token); throw new InvalidOperationException("cancel did not throw"); }
            catch (OperationCanceledException) { Check(pool.ProcessIds.Length == 0, "cancel reaps owned worker"); }
        }
        using (var pool = new DrawingWorkerPool(Options("block") with { SolveTimeout = TimeSpan.FromSeconds(2) }))
        {
            using var result = await Solve(pool);
            Check(result.Views.All(v => v.Error?.Contains("deadline", StringComparison.OrdinalIgnoreCase) == true), "request deadlines");
        }
        using (var pool = new DrawingWorkerPool(Options()))
        {
            pool.ObserveReply = r => r with { Identity = r.Identity with { Generation = r.Identity.Generation - 1 } };
            using var result = await Solve(pool);
            Check(result.Views.All(v => v.Error?.Contains("Stale worker reply", StringComparison.Ordinal) == true), "stale replies rejected");
        }
        try
        {
            using var client = await WorkerClient.StartAsync(Options() with { MemoryBytes = 2 * WorkerMemoryPolicy.GiB }, fingerprint with { RuntimeMajor = fingerprint.RuntimeMajor == 8 ? 10 : 8 }, default);
            throw new InvalidOperationException("runtime mismatch accepted");
        }
        catch (WorkerUnavailableException e) { Check(e.Message.Contains("fingerprint mismatch", StringComparison.OrdinalIgnoreCase), "runtime mismatch refused"); }
        using (var pool = new DrawingWorkerPool(Options("memory") with { MemoryBytes = WorkerMemoryPolicy.GiB,
            ReadMemory = () => new(4 * WorkerMemoryPolicy.GiB, 3 * WorkerMemoryPolicy.GiB) }))
        {
            using var result = await Solve(pool);
            Check(result.Views.All(v => v.Error is not null && !v.Error.Contains("Memory cap failed", StringComparison.Ordinal)), "job memory cap enforced");
        }
        using (var pool = new DrawingWorkerPool(Options() with { IdleTimeout = TimeSpan.FromMilliseconds(300) }))
        {
            pool.EncodedDrawing = (_, _) => { };
            using var result = await Solve(pool);
            for (var i = 0; i < 50 && pool.ProcessIds.Length != 0; i++) await Task.Delay(100);
            Check(pool.ProcessIds.Length == 0, "idle worker reaped");
        }
        File.WriteAllBytes(Path.Combine(folder, "lifecycle-results.json"), WorkerCodec.Encode(checks));
    }
    private static byte[] Identity(ViewDrawing drawing)
    {
        var dto = WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(drawing, true));
        return WorkerCodec.Encode(new { dto.Pieces, dto.Regions, dto.Values.Frame, dto.Values.Error, dto.Values.RoutingQuarantine, dto.Values.Report.Quarantined });
    }

    private static ViewRequest[] Views(RhinoDoc doc, string model, string mode, BoundingBox bounds)
    {
        if (!bounds.IsValid) bounds = new(new Point3d(-1, -1, -1), new Point3d(1, 1, 1));
        if (mode.StartsWith("case:", StringComparison.Ordinal))
        {
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(model, ".json")));
            var descriptor = metadata.RootElement.GetProperty("views")[int.Parse(mode[5..])];
            var id = descriptor.GetProperty("viewBox").GetProperty("objectId").GetGuid();
            return [ViewRequest.Parse(id, doc) with { Name = descriptor.GetProperty("name").GetString()! }];
        }
        if (mode == "full") return [new ViewRequest("Unbounded named Top", -Vector3d.ZAxis) { Kind = ViewSourceKind.NamedView,
            Named = new(Plane.WorldXY, new(-1, 1), new(-1, 1), false) { Target = Point3d.Origin } }];
        if (mode.EndsWith("storey", StringComparison.Ordinal)) return [ViewRequest.Parse(new Box(
            new Plane(new Point3d(12, 8, 18), Vector3d.ZAxis), new Interval(-13, 13), new Interval(-9, 9), new Interval(-2, 0)))];
        if (mode.EndsWith("elevation", StringComparison.Ordinal)) return [ViewRequest.Parse(new Box(
            new Plane(new Point3d(12, -.5, 18), Vector3d.XAxis, Vector3d.ZAxis), new Interval(-13, 13), new Interval(-2, 2), new Interval(-4, 0)))];
        if (mode == "section") return [ViewRequest.Parse(Plane.WorldXY)];
        var top = new Plane(new Point3d(bounds.Center.X, bounds.Center.Y, bounds.Max.Z + doc.ModelAbsoluteTolerance * 10), Vector3d.ZAxis);
        if (mode == "terrain") return [ViewRequest.Parse(new Rectangle3d(new Plane(new Point3d(.5, .5, bounds.Max.Z + .1), Vector3d.ZAxis), new Interval(-.45, .45), new Interval(-.45, .45)))];
        if (mode == "sectionbox") top = Plane.WorldXY;
        if (mode == "building") top.OriginZ = bounds.Min.Z + 1.2 * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var scale = mode.EndsWith("tenth", StringComparison.Ordinal) ? Math.Sqrt(.1) : mode == "ninety" ? Math.Sqrt(.9) : .5;
        var rectangle = new Rectangle3d(top, new Interval(-bounds.Diagonal.X * scale / 2, bounds.Diagonal.X * scale / 2),
            new Interval(-bounds.Diagonal.Y * scale / 2, bounds.Diagonal.Y * scale / 2));
        var view = ViewRequest.Parse(rectangle);
        return [view with { Cut = view.Cut! with { SourceDepth = Math.Max(doc.ModelAbsoluteTolerance * 10,
            -bounds.GetCorners().Min(top.DistanceTo) + doc.ModelAbsoluteTolerance * 10) } }];
    }
}
