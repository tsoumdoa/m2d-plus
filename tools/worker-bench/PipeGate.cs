using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Measurements;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

internal static class PipeGate
{
    private const string SettingsText = "hidden=on\ntangents=off\nseams=off\nfarclip=noline\nbeyond=off\nmin visible=0";
    private const double MarginMm = .5;
    // Exact archived rhino3dm recipe, evidence/pipe-poc-scaling-20261118/models/manifest.json.
    // RhinoCommon bounding-box implementations produce slightly different extents.
    private static readonly (double X, double Y, double Z) LatticeSpacing =
        (624.6750184008374, 464.3282399935208, 82.49185454824);
    private const string OwnerSha256 = "15ECB531E8DC242159C4D5B2E3227BB8CF7640023AA145037AFB19EB9F6AFFDA";
    private static readonly Lazy<NativeScheduler> Scheduler = new(() =>
        new(WorkerMemoryPolicy.Budget(WorkerMachineMemory.Read())));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    internal sealed record Fixture(string Name, string Path, string Sha256, string Layout, int Pipes);
    internal sealed record Band(string Fixture, string View, string ModelSha256, double WrongVisibleMm,
        double WrongVisiblePercent, double WrongHiddenMm, double WrongHiddenPercent,
        double ToleranceMm, double TolerancePercent, double HiddenToleranceMm, double HiddenTolerancePercent,
        int Observations = 0, string[]? ObservationArchives = null);
    internal sealed record Baseline(int Schema, string Settings, double SpacingMm, double DistanceMm,
        string RhinoBuild, int RuntimeMajor, Band[] Fixtures, string[]? CalibrationReceipts = null);
    internal sealed record Row(Fixture Fixture, string View, double PipesMs, double HldMedianMs,
        double[] HldMs, DrawingComparison.Result[] Comparisons, DrawingComparison.Result[] HldRepeatability,
        double WrongVisibleMm, double WrongVisiblePercent, double WrongHiddenMm, double WrongHiddenPercent,
        double PipesHiddenOverHldVisibleMm, DrawingComparison.Overlap[] PipesSelfOverlap, double MissingPipesMm,
        double MissingHldMm, double HldClassVariationMm, double HldMissingVariationMm,
        double WrongVisibleSpreadMm, double WrongVisibleSpreadPercent, double ToleranceMm,
        double TolerancePercent, double WrongHiddenSpreadMm, double WrongHiddenSpreadPercent,
        double HiddenToleranceMm, double HiddenTolerancePercent, bool Pass, string? Failure);

    internal static void CompareArchives(string pipes, string hld, string output)
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        if (File.Exists(output)) throw new IOException("Preserve existing comparison: " + output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var a = WorkerCodec.RestoreDrawing(File.ReadAllBytes(pipes));
        using var b = WorkerCodec.RestoreDrawing(File.ReadAllBytes(hld));
        if (!a.Frame.Equals(b.Frame)) throw new InvalidDataException("Compare drawings in the same projected frame");
        using var file = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(file, DrawingComparison.Compare(a, b), Json);
    }

    internal static void DiagnoseArchives(string model, string archives, string output)
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Preserve existing diagnosis: " + output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var pipes = WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(archives, "pipes.json")));
        var hlds = new List<ViewDrawing>();
        try
        {
            var count = Directory.GetFiles(archives, "hld-*.json").Length;
            for (var repeat = 0; repeat < count; repeat++)
            {
                var path = Path.Combine(archives, $"hld-{repeat}.json");
                if (!File.Exists(path)) throw new InvalidDataException("HLD archive sequence is incomplete");
                var hld = WorkerCodec.RestoreDrawing(File.ReadAllBytes(path)); hlds.Add(hld);
                if (!pipes.Frame.Equals(hld.Frame)) throw new InvalidDataException("Diagnosis frames differ");
            }
            if (hlds.Count == 0) throw new InvalidDataException("No HLD archives");
            Directory.CreateDirectory(output);
            PipeGateDiagnosis.Write(output, model, pipes, hlds, hlds.Select(h => DrawingComparison.Compare(pipes, h)).ToArray(), Path.GetFullPath(archives));
        }
        finally { foreach (var hld in hlds) hld.Dispose(); }
    }

    // pipe-gate <resolver> [--repeats N] [--baseline path] [--update-baseline] [--resume receipt-folder]
    internal static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") throw new InvalidOperationException("Set M2D_RHINO_TESTS=1");
        var repeats = 3; var update = false; string? resume = null;
        var calibrationPaths = new List<string>();
        var baselinePath = Path.GetFullPath("tools/worker-bench/pipe-gate-baseline.json");
        for (var i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--repeats": repeats = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--baseline": baselinePath = Path.GetFullPath(args[++i]); break;
                case "--update-baseline": update = true; break;
                case "--resume": resume = Path.GetFullPath(args[++i]); break;
                case "--calibrate-from": calibrationPaths.Add(Path.GetFullPath(args[++i])); break;
                default: throw new ArgumentException("Unknown pipe-gate option: " + args[i]);
            }
        if (repeats < 2) throw new ArgumentException("At least two HLD repeats are required to measure variation");
        if (calibrationPaths.Count > 0 && !update) throw new ArgumentException("--calibrate-from requires --update-baseline");
        if (calibrationPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != calibrationPaths.Count ||
            calibrationPaths.Any(p => string.Equals(p, resume, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Do not count the same receipt twice");
        var root = Environment.GetEnvironmentVariable("M2D_MODELS") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "m2d-plus-models");
        var folder = Path.Combine(root, "evidence", "pipe-gate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        if (Directory.Exists(folder)) throw new IOException("Preserve existing receipt: " + folder);
        Directory.CreateDirectory(folder);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        var fingerprint = WorkerCodec.Fingerprint();
        var baseline = update ? null : JsonSerializer.Deserialize<Baseline>(File.ReadAllBytes(baselinePath), Json)
            ?? throw new InvalidDataException("Missing baseline");
        if (baseline is not null && (baseline.Schema != 2 || baseline.Settings != SettingsText ||
            baseline.SpacingMm != DrawingComparison.SpacingMm || baseline.DistanceMm != DrawingComparison.DistanceMm ||
            baseline.RhinoBuild != fingerprint.RhinoBuild || baseline.RuntimeMajor != Environment.Version.Major))
            throw new InvalidDataException("Baseline environment/settings mismatch; review and explicitly --update-baseline");
        var commit = Git("rev-parse", "HEAD"); var dirty = Git("status", "--porcelain");
        var rows = new List<Row>(); var skips = new List<string>(); var failures = new List<string>();
        var fixtures = Generate(root); var skip320 = false;
        var calibration = new Dictionary<(string Fixture, string View), Dictionary<string, DrawingComparison.Result[]>>();
        foreach (var path in calibrationPaths)
        {
            using var archived = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path, "receipt.json")));
            var value = archived.RootElement;
            var archiveRepeats = value.GetProperty("HldRepeats").GetInt32();
            if (value.GetProperty("Schema").GetInt32() is not (1 or 2) || value.GetProperty("Settings").GetString() != SettingsText ||
                archiveRepeats < 2 || value.GetProperty("Skips").GetArrayLength() != 0 ||
                value.GetProperty("SpacingMm").GetDouble() != DrawingComparison.SpacingMm ||
                value.GetProperty("DistanceMm").GetDouble() != DrawingComparison.DistanceMm ||
                !fingerprint.Accepts(value.GetProperty("Fingerprint").Deserialize<WorkerFingerprint>(Json)!) ||
                !fixtures.SequenceEqual(value.GetProperty("Fixtures").Deserialize<Fixture[]>(Json)!))
                throw new InvalidDataException("Calibration environment/settings/fixtures mismatch: " + path);
            var seen = new HashSet<(string Fixture, string View)>();
            foreach (var row in value.GetProperty("Rows").EnumerateArray())
            {
                var fixture = row.GetProperty("Fixture").Deserialize<Fixture>(Json)!;
                var view = row.GetProperty("View").GetString()!; var key = (fixture.Name, view);
                var observations = row.GetProperty("Comparisons").Deserialize<DrawingComparison.Result[]>(Json)!;
                if (!fixtures.Contains(fixture) || view is not ("Top" or "SW" or "Box") || !seen.Add(key) || observations.Length != archiveRepeats)
                    throw new InvalidDataException("Invalid/duplicate calibration row: " + path);
                _ = RegressionBand.Observe(observations, MarginMm); // Reject non-finite/malformed buckets.
                if (!calibration.TryGetValue(key, out var origins))
                    calibration.Add(key, origins = new(StringComparer.OrdinalIgnoreCase));
                var origin = ResolveArchives(path, fixture.Name, view);
                if (origins.TryGetValue(origin, out var existing) && JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(observations, Json))
                    throw new InvalidDataException("Conflicting comparisons for the same drawing archives: " + origin);
                origins.TryAdd(origin, observations);
            }
            if (seen.Count != fixtures.Length * 3) throw new InvalidDataException("Calibration receipt is incomplete: " + path);
        }
        Band MakeBand(Row row)
        {
            var current = Path.Combine(folder, row.Fixture.Name, row.View);
            var origin = File.Exists(Path.Combine(current, "pipes.json")) ? current :
                ResolveArchives(resume ?? folder, row.Fixture.Name, row.View);
            var origins = calibration.TryGetValue((row.Fixture.Name, row.View), out var archived)
                ? new Dictionary<string, DrawingComparison.Result[]>(archived, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DrawingComparison.Result[]>(StringComparer.OrdinalIgnoreCase);
            if (origins.TryGetValue(origin, out var existing) && JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(row.Comparisons, Json))
                throw new InvalidDataException("Conflicting comparisons for the current drawing archives: " + origin);
            origins[origin] = row.Comparisons;
            var observations = origins.Values.SelectMany(values => values).ToArray();
            var band = RegressionBand.Observe(observations, MarginMm);
            return new(row.Fixture.Name, row.View, row.Fixture.Sha256, band.WrongVisibleMm, band.WrongVisiblePercent,
                band.WrongHiddenMm, band.WrongHiddenPercent, band.ToleranceMm, band.TolerancePercent,
                band.HiddenToleranceMm, band.HiddenTolerancePercent, observations.Length,
                origins.Keys.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order().ToArray());
        }
        string? Check(Fixture fixture, string name, double wrong, double percent, double hidden, double hiddenPercent)
        {
            if (update) return null;
            var band = baseline!.Fixtures.SingleOrDefault(b => b.Fixture == fixture.Name && b.View == name);
            return band is null ? "Missing fixture/view baseline" : band.ModelSha256 != fixture.Sha256 ? "Model SHA-256 changed" :
                !new RegressionBand(band.WrongVisibleMm, band.WrongVisiblePercent, band.WrongHiddenMm, band.WrongHiddenPercent,
                    band.ToleranceMm, band.TolerancePercent, band.HiddenToleranceMm, band.HiddenTolerancePercent).Accepts(wrong, percent, hidden, hiddenPercent)
                    ? "Wrong-visible or wrong-hidden exceeds baseline band" : null;
        }
        if (resume is not null)
        {
            using var previous = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(resume, "receipt.json")));
            var value = previous.RootElement;
            if (value.GetProperty("Schema").GetInt32() != 2 || value.GetProperty("Settings").GetString() != SettingsText ||
                value.GetProperty("HldRepeats").GetInt32() != repeats ||
                value.GetProperty("SpacingMm").GetDouble() != DrawingComparison.SpacingMm ||
                value.GetProperty("DistanceMm").GetDouble() != DrawingComparison.DistanceMm ||
                !fingerprint.Accepts(value.GetProperty("Fingerprint").Deserialize<WorkerFingerprint>(Json)!) ||
                !fixtures.SequenceEqual(value.GetProperty("Fixtures").Deserialize<Fixture[]>(Json)!))
                throw new InvalidDataException("Resume environment/settings/fixtures mismatch");
            rows.AddRange(value.GetProperty("Rows").Deserialize<Row[]>(Json)!.Where(r => r.Pass && r.Failure is null));
            if (rows.Any(r => r.Comparisons.Length != repeats || r.HldRepeatability.Length != repeats * (repeats - 1) / 2) ||
                rows.GroupBy(r => (r.Fixture.Name, r.View)).Any(g => g.Count() != 1))
                throw new InvalidDataException("Incomplete or duplicate resume rows");
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var failure = Check(row.Fixture, row.View, row.WrongVisibleMm, row.WrongVisiblePercent, row.WrongHiddenMm, row.WrongHiddenPercent);
                rows[i] = row with { Pass = failure is null, Failure = failure };
                if (failure is not null) failures.Add(row.Fixture.Name + "/" + row.View + ": " + failure);
            }
            skip320 = rows.Any(r => r.HldMs.Any(ms => ms > 120_000));
            Console.WriteLine($"Resumed {rows.Count} completed rows from {resume}");
        }
        void Receipt()
        {
            File.WriteAllText(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(new
            {
                Schema = 2, Utc = DateTime.UtcNow, Commit = commit, WorkingTree = dirty, Fingerprint = fingerprint,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                HldMultipleThreads = true, ProcessorCount = Environment.ProcessorCount, HldPolicy = HldProjection.Policy,
                Settings = SettingsText, HldRepeats = repeats, DrawingComparison.SpacingMm, DrawingComparison.DistanceMm,
                MarginMm, BaselinePath = baselinePath, UpdateBaseline = update, ResumedFrom = resume,
                CalibrationReceipts = calibrationPaths, CalibrationBands = calibrationPaths.Count == 0 ? null : rows.Select(MakeBand).ToArray(),
                BaselineCalibrationReceipts = baseline?.CalibrationReceipts,
                Execution = "owned headless host; worker admission; one native solve at a time",
                NativeBudgetBytes = Scheduler.Value.BudgetBytes,
                TimingScope = "per-view engine time including culling/admission/output; excludes startup, capture, scene preparation, tolerance census, codec, comparison and IO",
                Fixtures = fixtures, Rows = rows, Skips = skips, Failures = failures
            }, Json));
            File.WriteAllText(Path.Combine(folder, "receipt.md"), Markdown(rows, skips, failures, commit) +
                (resume is null ? "" : "\nCompleted rows and their drawing archives resumed from: " + resume + "\n") +
                (calibrationPaths.Count == 0 ? "" : "\nBaseline calibration pools the current/resumed comparisons with these fingerprint-matched receipts (schema 1 buckets are read in the corrected direction):\n\n" +
                    string.Join("\n", calibrationPaths.Select(p => "- " + p)) +
                    "\n\nEach error uses its own pooled maximum and range + 0.5 mm; unrelated HLD class variation never enters the bands. CalibrationBands in JSON records the resulting limits and observation counts.\n\n" +
                    "| Fixture/view | Observations | Wrong-visible limit mm/% | Wrong-hidden limit mm/% |\n|---|---:|---:|---:|\n" +
                    string.Join("\n", rows.Select(MakeBand).Select(b => FormattableString.Invariant(
                        $"| {b.Fixture}/{b.View} | {b.Observations} | {b.WrongVisibleMm + b.ToleranceMm:F3}/{b.WrongVisiblePercent + b.TolerancePercent:F6} | {b.WrongHiddenMm + b.HiddenToleranceMm:F3}/{b.WrongHiddenPercent + b.HiddenTolerancePercent:F6} |"))) + "\n"));
        }
        Receipt();
        foreach (var fixture in fixtures)
        {
            if (new[] { "Top", "SW", "Box" }.All(v => rows.Any(r => r.Fixture.Name == fixture.Name && r.View == v))) continue;
            if (fixture.Pipes == 320 && skip320) { skips.Add(fixture.Name + ": skipped because an HLD solve exceeded 120 s"); Receipt(); continue; }
            using var doc = RhinoDoc.OpenHeadless(fixture.Path);
            using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
            using var capture = new SceneCapture(doc, watch, null, cache);
            while (!capture.Step(TimeSpan.FromSeconds(30))) { }
            using var scene = capture.Accept();
            foreach (var name in new[] { "Top", "SW", "Box" })
            {
                if (rows.Any(r => r.Fixture.Name == fixture.Name && r.View == name)) continue;
                Console.WriteLine($"START {fixture.Name}/{name}");
                var hlds = new List<ViewDrawing>();
                try
                {
                    using var pipes = Solve(scene.Value, name, "pipes");
                    var archives = Path.Combine(folder, fixture.Name, name); Directory.CreateDirectory(archives);
                    File.WriteAllBytes(Path.Combine(archives, "pipes.json"), WorkerCodec.CaptureDrawing(pipes));
                    if (pipes.Report.Route != "pipes (experimental)")
                        throw new InvalidOperationException("Pipes fell back: " + pipes.Info);
                    for (var repeat = 0; repeat < repeats; repeat++)
                    {
                        var hld = Solve(scene.Value, name, "hld"); hlds.Add(hld);
                        File.WriteAllBytes(Path.Combine(archives, $"hld-{repeat}.json"), WorkerCodec.CaptureDrawing(hld));
                        Console.WriteLine($"  HLD {repeat + 1}: {hld.Seconds * 1000:F1} ms");
                        if (hld.Seconds > 120) skip320 = true;
                    }
                    var overlap = DrawingComparison.SelfOverlap(pipes);
                    var comparisons = hlds.Select(hld => DrawingComparison.Compare(pipes, hld)).ToArray();
                    // Every unordered pair, with both sampling directions retained.
                    var variation = new List<DrawingComparison.Result>();
                    for (var a = 0; a < hlds.Count; a++)
                    for (var b = a + 1; b < hlds.Count; b++) variation.Add(DrawingComparison.Compare(hlds[a], hlds[b]));
                    var wrong = comparisons.Max(c => c.WrongVisibleMm); var percent = comparisons.Max(c => c.WrongVisiblePercent);
                    var hidden = comparisons.Max(c => c.WrongHiddenMm); var hiddenPercent = comparisons.Max(c => c.WrongHiddenPercent);
                    var hiddenSpread = hidden - comparisons.Min(c => c.WrongHiddenMm);
                    var hiddenSpreadPercent = hiddenPercent - comparisons.Min(c => c.WrongHiddenPercent);
                    var spread = wrong - comparisons.Min(c => c.WrongVisibleMm);
                    var spreadPercent = percent - comparisons.Min(c => c.WrongVisiblePercent);
                    var classVariation = variation.Max(c => Math.Max(
                        c.PipesToHld.Classes.Sum(r => r.OppositeOnly), c.HldToPipes.Classes.Sum(r => r.OppositeOnly)));
                    var missingVariation = variation.Max(c => Math.Max(c.PipesToHld.MissingMm, c.HldToPipes.MissingMm));
                    // Each direction uses its own directly observed error range. Unrelated HLD
                    // missing/class noise does not loosen either band.
                    var tolerance = spread + MarginMm;
                    var visible = comparisons[0].PipesToHld[LineClass.Visible].Length;
                    var tolerancePercent = spreadPercent + (visible == 0 ? 0 : 100 * MarginMm / visible);
                    var hiddenTolerance = hiddenSpread + MarginMm;
                    var hiddenTolerancePercent = hiddenSpreadPercent + comparisons.Max(c =>
                        c.HldToPipes[LineClass.Visible].Length == 0 ? 0 : 100 * MarginMm / c.HldToPipes[LineClass.Visible].Length);
                    var failure = Check(fixture, name, wrong, percent, hidden, hiddenPercent);
                    rows.Add(new(fixture, name, pipes.Seconds * 1000, Median(hlds.Select(h => h.Seconds * 1000)),
                        hlds.Select(h => h.Seconds * 1000).ToArray(), comparisons, variation.ToArray(), wrong, percent,
                        hidden, hiddenPercent, comparisons.Max(c => c.PipesHiddenOverHldVisibleMm), overlap, comparisons.Max(c => c.PipesToHld.MissingMm),
                        comparisons.Max(c => c.HldToPipes.MissingMm), classVariation, missingVariation, spread, spreadPercent,
                        tolerance, tolerancePercent, hiddenSpread, hiddenSpreadPercent, hiddenTolerance, hiddenTolerancePercent, failure is null, failure));
                    if (fixture.Name == "owner" && name is "Top" or "SW")
                        PipeGateDiagnosis.Write(archives, fixture.Path, pipes, hlds, comparisons);
                    if (failure is not null) failures.Add(fixture.Name + "/" + name + ": " + failure);
                    Console.WriteLine($"  wrong-visible {wrong:F3} mm ({percent:F6}%), wrong-hidden {hidden:F3} mm ({hiddenPercent:F6}%), HLD class variation {classVariation:F3} mm");
                    Receipt();
                }
                catch (Exception ex)
                { failures.Add(fixture.Name + "/" + name + ": " + ex); Receipt(); Console.Error.WriteLine(ex); Console.WriteLine("Receipt: " + folder); return 1; }
                finally { foreach (var hld in hlds) hld.Dispose(); }
                if (fixture.Pipes == 320 && skip320)
                {
                    foreach (var remaining in new[] { "Top", "SW", "Box" }.SkipWhile(v => v != name).Skip(1))
                        skips.Add(fixture.Name + "/" + remaining + ": skipped because an HLD solve exceeded 120 s");
                    break;
                }
            }
        }
        if (baseline is not null && baseline.Fixtures.Any(b => !rows.Any(r => r.Fixture.Name == b.Fixture && r.View == b.View)))
            failures.Add("Baseline contains unmeasured rows (see skips)");
        if (update && failures.Count == 0)
        {
            // A partial run must never silently delete existing baseline rows.
            if (skips.Count > 0) failures.Add("Partial run: baseline not updated");
            else File.WriteAllText(baselinePath, JsonSerializer.Serialize(new Baseline(2, SettingsText,
                DrawingComparison.SpacingMm, DrawingComparison.DistanceMm, fingerprint.RhinoBuild, Environment.Version.Major,
                rows.Select(MakeBand).ToArray(), new[] { folder }.Concat(calibrationPaths)
                    .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToArray()), Json));
        }
        Receipt(); Console.WriteLine("Receipt: " + folder); return failures.Count == 0 ? 0 : 2;
    }
    private static ViewDrawing Solve(PreparedScene scene, string name, string engine)
    {
        object source = name == "Box" ? new Box(new Plane(new Point3d(65986, 40014, 14669), Vector3d.ZAxis),
            new Interval(-1500, 1500), new Interval(-1500, 1500), new Interval(-3000, 3000)) : name;
        var view = ViewRequest.Parse(source) with { Name = name };
        view = view with { Settings = Settings.Resolve([new(name, name, view.Kind, view.Cut is not null,
            scene.Bounds.Diagonal.Length, scene.Tolerance, view.Cut?.SourceDepth)],
            [[SettingsText + "\nvisibility=" + engine]], DocumentUnits.Millimetres).Views[0] };
        using var batch = NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision, "pipe-gate", engine, 1),
            // This process owns its headless RhinoCore; apply worker admission,
            // using the worker RAM policy and one process-wide native scheduler.
            [view], new() { IsWorker = true, Scheduler = Scheduler.Value }, default, null).GetAwaiter().GetResult();
        var result = batch.Views[0];
        if (result.Error is not null || result.Report.Quarantined) throw new InvalidOperationException(result.Info);
        // Detach while preserving timing; no presentation/filter/placement or disk cache.
        return WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(result));
    }
    private static Fixture[] Generate(string root)
    {
        var owner = Path.Combine(root, "owner", "pipes-node-gdas.3dm");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(owner))) != OwnerSha256)
            throw new InvalidDataException("Owner model SHA-256 changed; the pinned scaling recipe needs review");
        using var original = File3dm.Read(owner) ?? throw new IOException("Cannot read " + owner);
        if (original.Settings.ModelUnitSystem != UnitSystem.Millimeters) throw new InvalidDataException("Fixture must use millimetres");
        var bodies = original.Objects.Where(o => o.Geometry is Brep).ToArray();
        if (bodies.Length != 5) throw new InvalidDataException("Expected five owner pipes");
        var folder = Path.Combine(root, "generated", "pipe-gate-v3"); Directory.CreateDirectory(folder);
        Fixture Describe(string name, string path, string layout, int pipes) => new(name, path,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), layout, pipes);
        var fixtures = new List<Fixture> { Describe("owner", owner, "original; includes loose curves and point", 5) };
        foreach (var (xCount, yCount, zCount) in new[] { (1, 1, 1), (2, 1, 2), (4, 2, 2), (4, 4, 4) })
        foreach (var lateral in new[] { false, true })
        {
            var copies = xCount * yCount * zCount; var layout = lateral ? "lateral" : "stacked";
            var name = $"{copies * 5}-{layout}"; var path = Path.Combine(folder, name + ".3dm");
            if (!File.Exists(path))
            {
                using var model = new File3dm(); model.Settings.ModelUnitSystem = UnitSystem.Millimeters;
                model.Settings.ModelAbsoluteTolerance = original.Settings.ModelAbsoluteTolerance;
                model.Settings.ModelAngleToleranceRadians = original.Settings.ModelAngleToleranceRadians;
                var copy = 0;
                for (var x = 0; x < xCount; x++)
                for (var y = 0; y < yCount; y++)
                for (var z = 0; z < zCount; z++, copy++)
                foreach (var body in bodies)
                {
                    using var geometry = body.Geometry.Duplicate();
                    var delta = lateral ? new Vector3d(copy * LatticeSpacing.X * 10, 0, 0) :
                        new Vector3d(x * LatticeSpacing.X, y * LatticeSpacing.Y, z * LatticeSpacing.Z);
                    if (!geometry.Transform(Transform.Translation(delta)) || geometry is not Brep { IsValid: true, IsSolid: true })
                        throw new InvalidDataException("Invalid generated pipe");
                    using var attributes = new global::Rhino.DocObjects.ObjectAttributes
                    {
                        ObjectId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"pipe-gate-v3/{name}/{copy}/{body.Id}")).AsSpan(0, 16)),
                        Name = $"{x},{y},{z}|{body.Id}"
                    };
                    model.Objects.Add(geometry, attributes);
                }
                if (!model.Write(path, 8)) throw new IOException("Could not save " + path);
            }
            using var check = File3dm.Read(path) ?? throw new IOException("Cannot re-read " + path);
            if (check.Objects.Count != copies * 5 || check.Objects.Any(o => o.Geometry is not Brep { IsValid: true, IsSolid: true }))
                throw new InvalidDataException("Generated fixture validation failed: " + path);
            fixtures.Add(Describe(name, path, lateral ?
                FormattableString.Invariant($"world X separated by {LatticeSpacing.X * 10:R} mm; no Z stacking") :
                FormattableString.Invariant($"{xCount}x{yCount}x{zCount}; archived spacing mm={LatticeSpacing.X:R},{LatticeSpacing.Y:R},{LatticeSpacing.Z:R}"), copies * 5));
        }
        return fixtures.ToArray();
    }
    private static double Median(IEnumerable<double> values)
    { var sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
    private static string ResolveArchives(string receipt, string fixture, string view)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(receipt = Path.GetFullPath(receipt)))
        {
            var folder = Path.Combine(receipt, fixture, view);
            if (File.Exists(Path.Combine(folder, "pipes.json")) && File.Exists(Path.Combine(folder, "hld-0.json")) &&
                File.Exists(Path.Combine(folder, "hld-1.json"))) return folder;
            using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(receipt, "receipt.json")));
            receipt = data.RootElement.GetProperty("ResumedFrom").GetString() ??
                throw new InvalidDataException("Missing drawing archives for calibration: " + folder);
        }
        throw new InvalidDataException("Cyclic receipt ancestry");
    }
    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var result = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("git metadata failed"); return result.Trim();
    }
    private static string Markdown(List<Row> rows, List<string> skips, List<string> failures, string commit)
    {
        var text = new StringBuilder($"# Pipes regression gate\n\nCommit: `{commit}` (working tree and full environment in JSON). All lengths are projected mm.\n\n" +
            "HLD is a reference, not truth. Duplicate strokes are length weighted. Points are separate; hidden points suppressed by an engine appear as point absence, not a class swap.\n\n" +
            "Worst wrong-visible/wrong-hidden/missing across HLD repeats; Pipes is run once. HLD variation is the maximum directional class-swap length over every pair. Each gate band is its own observed error range plus 0.5 mm (and equivalent percentage points). Wrong-hidden samples HLD Visible with only Pipes Hidden nearby; its denominator is HLD Visible. The separate Pipes Hidden diagnostic samples from the Pipes side with only HLD Visible nearby. Self-overlap counts each Pipes stroke near another same-class piece (including both copies), excludes itself, and is diagnostic only. Times are per-view engine time including culling/admission/output, excluding startup, capture, scene preparation, tolerance census, codec, comparison and IO.\n\n" +
            "| Fixture/view | Pipes ms | HLD median ms | Wrong-visible mm | % | Wrong-hidden mm | % | Pipes self-overlap mm | Pipes Hidden over HLD Visible mm | Missing Pipes/HLD mm | HLD class/missing variation mm | Error ranges visible/hidden mm | Pass |\n" +
            "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n");
        foreach (var r in rows) text.AppendLine(FormattableString.Invariant($"| {r.Fixture.Name}/{r.View} | {r.PipesMs:F1} | {r.HldMedianMs:F1} | {r.WrongVisibleMm:F3} | {r.WrongVisiblePercent:F6} | {r.WrongHiddenMm:F3} | {r.WrongHiddenPercent:F6} | {r.PipesSelfOverlap.Sum(o => o.Mm):F3} | {r.PipesHiddenOverHldVisibleMm:F3} | {r.MissingPipesMm:F3}/{r.MissingHldMm:F3} | {r.HldClassVariationMm:F3}/{r.HldMissingVariationMm:F3} | {r.WrongVisibleSpreadMm:F3}/{r.WrongHiddenSpreadMm:F3} | {r.Pass} |"));
        text.AppendLine("\nUnresolved and point buckets (maxima over references; P/H = Pipes/HLD):\n\n" +
            "| Fixture/view | Unresolved P/H mm | Point class disagreements P/H | Point absence P/H |\n" +
            "|---|---:|---:|---:|");
        foreach (var r in rows)
            text.AppendLine(FormattableString.Invariant($"| {r.Fixture.Name}/{r.View} | {r.Comparisons.Max(c => c.PipesToHld.UnresolvedMm):F3}/{r.Comparisons.Max(c => c.HldToPipes.UnresolvedMm):F3} | {r.Comparisons.Max(c => c.PipesToHld.Points.ClassDisagreements)}/{r.Comparisons.Max(c => c.HldToPipes.Points.ClassDisagreements)} | {r.Comparisons.Max(c => c.PipesToHld.Points.Absent)}/{r.Comparisons.Max(c => c.HldToPipes.Points.Absent)} |"));
        text.AppendLine("\nEvery repeat, zero-length projection count and class bucket is retained in receipt.json.");
        foreach (var skip in skips) text.AppendLine("\nSkipped: " + skip);
        foreach (var failure in failures) text.AppendLine("\nFailure: " + failure);
        return text.ToString();
    }
}
