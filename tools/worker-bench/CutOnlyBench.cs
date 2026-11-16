using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

/// <summary>Serial, fresh normal/on/off solves over one retained scene. Evidence stays outside git.</summary>
internal static class CutOnlyBench
{
    internal static void Run(string model, string output, string mode)
    {
        if (Directory.Exists(output)) throw new IOException("Choose a new evidence directory: " + output);
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc = RhinoDoc.OpenHeadless(model); using var watch = new DocumentWatch(doc); using var cache = new SceneCache();
        var clock = Stopwatch.StartNew(); using var capture = new SceneCapture(doc, watch, null, cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(20))) { }
        using var sceneLease = capture.Accept(); var scene = sceneLease.Value;
        var captureSeconds = clock.Elapsed.TotalSeconds;
        var units = doc.ModelUnitSystem == UnitSystem.Millimeters ? DocumentUnits.Millimetres : DocumentUnits.Metres;
        var views = new List<ViewRequest>();
        bool poolMode = mode.StartsWith("pool:", StringComparison.Ordinal);
        if (poolMode || mode.StartsWith("offset:", StringComparison.Ordinal))
        {
            double offset = double.Parse(mode[(poolMode ? 5 : 7)..], System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < 10; i++) views.Add(ViewRequest.Parse(new Plane(new Point3d(offset, offset, offset + 1500 + i * 3200), Vector3d.ZAxis)) with { Name = "Plan " + i });
        }
        else
        {
            // Explicit planes match ScaleBench/EngineFastBench. Fixture named views can shadow same-named clipping planes.
            if (mode != "long") views.Add(ViewRequest.Parse(new Plane(new Point3d(scene.Bounds.Center.X, scene.Bounds.Center.Y,
                1.2 * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem)), Vector3d.ZAxis)) with { Name = "Plan 1.2m" });
            views.Add(ViewRequest.Parse(new Plane(scene.Bounds.Center, Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Long section" });
        }
        var rows = new List<object>();
        void Save() => File.WriteAllBytes(Path.Combine(output, "timings.json"), WorkerCodec.Encode(new
        { model, modelHash = WorkerCodec.Hash(File.ReadAllBytes(model)), mode, captureSeconds, fingerprint = WorkerCodec.Fingerprint(), rows }));
        Save();
        if (poolMode)
        {
            using var pool = new DrawingWorkerPool(WorkerOptions.FromEnvironment() with
            { Directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin/Release/net8.0-windows")) });
            var normalRegions = new List<byte[]>(); var normalHashes = new List<string>();
            foreach (string label in new[] { "normal", "cut-only", "off-again" })
            {
                bool only = label == "cut-only";
                var requests = views.Select(v => v with { Settings = Settings.Resolve(
                    [new("v", v.Name, v.Kind, true, scene.Bounds.Diagonal.Length, scene.Tolerance)],
                    [[$"cutonly={(only ? "on" : "off")}\nhidden=on"]], units).Views[0].ForVisibilitySolve() }).ToArray();
                clock.Restart();
                using var batch = pool.SolveAsync(scene, new(scene.Revision, "cutonly-bench", label, 1), requests, new(), default).GetAwaiter().GetResult();
                double seconds = clock.Elapsed.TotalSeconds;
                var hashes = new List<string>();
                for (int i = 0; i < batch.Views.Count; i++)
                {
                    var drawing = batch.Views[i];
                    if (drawing.Error is { } error) throw new InvalidOperationException(error);
                    var bytes = WorkerCodec.CaptureDrawing(drawing, true); var dto = WorkerCodec.Decode<WorkerCodec.Drawing>(bytes);
                    var regions = WorkerCodec.Encode(dto.Regions); var hash = WorkerCodec.Hash(bytes); hashes.Add(hash);
                    if (label == "normal") { normalRegions.Add(regions); normalHashes.Add(hash); }
                    if (!regions.SequenceEqual(normalRegions[i]) || label == "off-again" && hash != normalHashes[i])
                        throw new InvalidOperationException("Worker strict section/normal equality failed");
                    if (only && (drawing.NativeCalls != 0 || drawing.KernelEdges != 0 || drawing.Items.Any(p => p.Class != LineClass.Unresolved) ||
                        drawing.Phases.Seconds.Keys.Any(p => p is "native solve" or "kernel solve" or "native ray verification")))
                        throw new InvalidOperationException("Worker cut-only projection was not skipped");
                    File.WriteAllBytes(Path.Combine(output, $"{i}-{label}.json"), WorkerCodec.CaptureDrawing(drawing));
                }
                rows.Add(new { label, wallSeconds = seconds, sizing = pool.LastSizing, hashes, regionMatch = true, normalMatch = label == "off-again" }); Save();
                Console.WriteLine($"Worker pool {label}: {seconds:F3}s; {pool.LastSizing}; all Cut/Hatch bytes equal; off-again hashes equal");
            }
            return;
        }
        for (int i = 0; i < views.Count; i++)
        {
            byte[]? reference = null; string? normalHash = null;
            // Repeat off last: proves the intervening cut-only run has not mutated shared data.
            foreach (string label in new[] { "normal", "cut-only", "off-again" })
            {
                bool only = label == "cut-only"; var view = views[i];
                var resolved = Settings.Resolve([new("v", view.Name, view.Kind, true, scene.Bounds.Diagonal.Length, scene.Tolerance)],
                    [[$"cutonly={(only ? "on" : "off")}\nhidden=on"]], units);
                if (resolved.HasErrors) throw new InvalidOperationException("Settings resolution failed");
                view = view with { Settings = resolved.Views[0] };
                var identity = new RequestIdentity(scene.Revision, view.Revision, "cutonly-bench", 1);
                clock.Restart();
                // Same isolated benchmark admission as EngineFastBench; product dispatch is unchanged.
                using var raw = NativeDrawing.SolveBorrowedAsync(scene, identity, [view with { Settings = view.Settings.ForVisibilitySolve() }],
                    new() { IsWorker = true, Scheduler = new(12L * 1024 * 1024 * 1024) }, default, null).GetAwaiter().GetResult();
                double computeSeconds = clock.Elapsed.TotalSeconds; clock.Restart();
                using var displayed = DrawingPresentation.Apply(raw, identity, [view], []);
                double presentationSeconds = clock.Elapsed.TotalSeconds; var drawing = displayed.Views[0];
                if (drawing.Error is { } error) throw new InvalidOperationException(error);
                if (drawing.Regions.Count == 0) throw new InvalidOperationException("Expected a nonempty cut fixture");
                var bytes = WorkerCodec.CaptureDrawing(drawing, true); var dto = WorkerCodec.Decode<WorkerCodec.Drawing>(bytes);
                var regions = WorkerCodec.Encode(dto.Regions); var hash = WorkerCodec.Hash(bytes);
                if (label == "normal") { reference = regions; normalHash = hash; }
                bool regionMatch = reference!.SequenceEqual(regions);
                bool? normalMatch = label == "off-again" ? hash == normalHash : null;
                if (!regionMatch || normalMatch == false) throw new InvalidOperationException("Strict section/normal equality failed");
                if (only && (drawing.NativeCalls != 0 || drawing.KernelEdges != 0 || drawing.Items.Any(p => p.Class != LineClass.Unresolved) ||
                    drawing.Phases.Seconds.Keys.Any(p => p is "native solve" or "kernel solve" or "native ray verification")))
                    throw new InvalidOperationException("Cut-only projection was not skipped");
                rows.Add(new { i, view.Name, label, computeSeconds, presentationSeconds, wallSeconds = computeSeconds + presentationSeconds,
                    regionMatch, normalMatch, sectionHash = WorkerCodec.Hash(regions), hash, drawing.NativeCalls, drawing.KernelEdges,
                    regions = drawing.Regions.Count, hatches = drawing.Regions.Count(r => r.Region.Hatch is not null),
                    pieces = drawing.Items.Count, phases = drawing.Phases.Seconds, info = drawing.Info });
                File.WriteAllBytes(Path.Combine(output, $"{i}-{label}.json"), WorkerCodec.CaptureDrawing(drawing)); Save();
                Console.WriteLine($"{view.Name} {label}: {computeSeconds + presentationSeconds:F3}s; regions={drawing.Regions.Count}; Cut/Hatch bytes equal={regionMatch}; off hash={normalMatch}");
            }
        }
    }
}
