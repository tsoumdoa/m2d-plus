using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using M2DPlus.Core;
using M2DPlus.Core.Diagnostics;
using Rhino;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>The platform self-test (docs/design/macos.md §Self-test): environment, memory probe, route and admission,
/// the built-in fixture drawn through the production dispatch and compared with the committed Windows baseline, and
/// cooperative cancellation. Run on the Rhino/GH UI thread; it blocks while the fixture draws (a few seconds).
/// It only creates a private headless document and never touches the active document.</summary>
public static class PlatformSelfTest
{
    public const string Fixture = "selftest-v1";
    internal const string BaselineResource = "M2DPlus.Rhino.SelfTest.Baseline.json";

    public sealed record Check(string Name, bool Passed, string Detail);

    public sealed class Report
    {
        internal Report(List<Check> checks, IReadOnlyList<DrawingSignature> signatures) { Checks = checks; Signatures = signatures; }
        public List<Check> Checks { get; }
        public IReadOnlyList<DrawingSignature> Signatures { get; }
        public bool Passed => Checks.All(c => c.Passed);
        public string Summary => $"M2D+ platform self-test: {(Passed ? "PASS" : "FAIL")} ({Checks.Count(c => c.Passed)}/{Checks.Count} checks passed)";
        public string Text => Summary + Environment.NewLine + string.Join(Environment.NewLine,
            Checks.Select(c => $"[{(c.Passed ? "pass" : "FAIL")}] {c.Name}: {c.Detail}"));
    }

    /// <summary>Runs every non-UI check. <paramref name="baseline"/> overrides the embedded baseline (tests).</summary>
    public static Report Run(SignatureBaseline? baseline = null)
    {
        var checks = new List<Check>();
        IReadOnlyList<DrawingSignature> signatures = [];
        void Try(string name, Func<Check> check)
        {
            try { checks.Add(check()); }
            catch (Exception e) { checks.Add(new(name, false, e.GetType().Name + ": " + e.Message)); }
        }
        Try("platform", Platform);
        Try("memory probe", MemoryProbe);
        Try("worker route", WorkerRoute);
        Try("admission", Admission);
        Try("fixture drawing", () =>
        {
            var clock = Stopwatch.StartNew();
            using var batch = DrawFixture(default);
            signatures = batch.Views.Select(v => Signature(v)).ToArray();
            var problems = batch.Views.Where(v => v.Error is not null).Select(v => $"{v.Name}: {v.Error}").ToList();
            if (!HostPlatform.IsolatedWorkers)
                problems.AddRange(batch.Views.Where(v => !v.Diagnostics.Any(d => d.StartsWith("Route: in Rhino's process", StringComparison.Ordinal)))
                    .Select(v => $"{v.Name}: Info lacks the in-process route disclosure"));
            problems.AddRange(batch.Views.Where(v => v.NativeCalls == 0 && v.Settings?.CutOnly != true).Select(v => $"{v.Name}: HLD did not run"));
            var threads = batch.Views.All(v => v.MultipleThreads) ? "multithreaded HLD" : "single-threaded HLD";
            return new("fixture drawing", problems.Count == 0, problems.Count == 0
                ? Invariant($"{batch.Views.Count} views drawn in {clock.Elapsed.TotalSeconds:F1} s ({threads}); ") + string.Join("; ", batch.Views.Select(v =>
                    Invariant($"{v.Name} {v.Items.Count} pieces/{v.Regions.Count} regions")))
                : string.Join("; ", problems));
        });
        Try("fixture vs Windows baseline", () =>
        {
            if (signatures.Count == 0) return new("fixture vs Windows baseline", false, "No fixture drawing to compare.");
            var expected = baseline ?? EmbeddedBaseline();
            if (expected is null) return new("fixture vs Windows baseline", false, "No embedded baseline in this build.");
            var results = SignatureComparison.Compare(expected, signatures);
            var detail = string.Join("; ", results.Select(r => r.View + ": " + (r.Passed ? "match" : string.Join(", ", r.Failures)) +
                (r.Notes.Count > 0 ? " [" + string.Join(", ", r.Notes) + "]" : "")));
            return new("fixture vs Windows baseline", results.All(r => r.Passed), $"baseline {expected.Producer}; " + detail);
        });
        Try("cooperative cancellation", Cancellation);
        return new(checks, signatures);
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static Check Platform()
    {
        var rhino = typeof(RhinoApp).Assembly;
        var ours = typeof(PlatformSelfTest).Assembly;
        var windowsBuild = ours.GetCustomAttributes<TargetPlatformAttribute>().Any(a => a.PlatformName.StartsWith("Windows", StringComparison.OrdinalIgnoreCase));
        var framework = ours.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";
        var detail = $"{HostPlatform.Describe()}; Rhino {RhinoApp.Version} ({RhinoApp.ExeServiceRelease}); RhinoCommon {rhino.GetName().Version} at {rhino.Location}; " +
            $"M2D+ {ours.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} built for {framework}{(windowsBuild ? " (Windows build)" : "")} at {ours.Location}";
        // On a Mac the cross-platform build must be loaded, and the route must be in-process only.
        var ok = HostPlatform.Os != HostOs.MacOS || (!windowsBuild && !HostPlatform.IsolatedWorkers);
        return new("platform", ok, detail);
    }

    private static Check MemoryProbe()
    {
        var clock = Stopwatch.StartNew();
        var memory = WorkerMachineMemory.Read();
        var elapsed = clock.Elapsed.TotalMilliseconds;
        var ok = memory.PhysicalBytes > 0 && memory.AvailableBytes > 0 && memory.AvailableBytes <= memory.PhysicalBytes;
        const double G = WorkerMemoryPolicy.GiB;
        return new("memory probe", ok, Invariant(
            $"physical {memory.PhysicalBytes / G:F2} GiB, available {memory.AvailableBytes / G:F2} GiB ({elapsed:F1} ms); in-process budget now {InProcessPolicy.Budget(memory) / G:F2} GiB, ") +
            Invariant($"scheduler ceiling {HostPlatform.Scheduler.BudgetBytes / G:F2} GiB; compare with `sysctl hw.memsize` and `vm_stat` (free + inactive pages)"));
    }

    private static Check WorkerRoute()
    {
        var installed = DrawingWorkerPool.Shared.Installed;
        var folder = Path.Combine(Path.GetDirectoryName(typeof(WorkerOptions).Assembly.Location)!, "workers");
        var ok = HostPlatform.IsolatedWorkers || !installed;
        return new("worker route", ok, HostPlatform.IsolatedWorkers
            ? $"isolated workers supported; matching worker {(installed ? "installed" : "missing")}"
            : $"in-process only (no Rhino.Inside on this platform); worker installed={installed}; workers folder {(Directory.Exists(folder) ? "present (unexpected in the mac package)" : "absent")}");
    }

    private static Check Admission()
    {
        var memory = new MachineMemory(16 * WorkerMemoryPolicy.GiB, 8 * WorkerMemoryPolicy.GiB);
        var small = new SceneCost(1_000_000, 100, 1000, 10);
        var huge = new SceneCost(1_000_000, 700_000, 0, 10);
        var ok = InProcessPolicy.Refusal(small, 4, memory) is null && InProcessPolicy.Refusal(huge, 1, memory) is not null;
        return new("admission", ok, ok ? "small 4-view set admitted; 700k-face scene refused with a message (16 GiB model machine)" : "unexpected admission result");
    }

    private static Check Cancellation()
    {
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        double? cancelledAt = null; string? cancelledIn = null;
        var progress = new InlineProgress(p =>
        {
            // Cancel inside HiddenLineDrawing.Compute: only Rhino's own progress callback carries a native fraction.
            // If the fixture is too quick for one, cancel between views and say the check was inconclusive.
            if (p.Phase != JobPhase.Drawing || source.IsCancellationRequested) return;
            if (p.Step == ViewSteps.Native && p.StepFraction is not null) cancelledIn = $"inside native HLD at {p.StepFraction:P0}";
            else if (p.Done >= 1) cancelledIn = $"between views, after view {p.Done} (inconclusive: no native progress callback came first)";
            else return;
            cancelledAt = clock.Elapsed.TotalSeconds; source.Cancel();
        });
        try
        {
            using var batch = DrawFixture(source.Token, progress);
            return new("cooperative cancellation", false, "The cancelled solve published a batch.");
        }
        catch (OperationCanceledException)
        {
            var returned = clock.Elapsed.TotalSeconds;
            // The scheduler must be free again: the next small solve starts and finishes.
            using var again = DrawFixture(default, null, views: 1);
            var ok = cancelledAt is not null && again.Views.All(v => v.Error is null);
            return new("cooperative cancellation", ok, Invariant(
                $"cancelled at {cancelledAt ?? -1:F2} s ({cancelledIn}), call returned at {returned:F2} s (the native call it was in may finish first); a following solve succeeded"));
        }
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }

    /// <summary>The fixture views, in order. Names are stable keys of the baseline.</summary>
    internal static ViewRequest[] FixtureViews()
    {
        var views = new[]
        {
            ViewRequest.Parse("Top") with { Name = "plan" },
            ViewRequest.Parse("Front") with { Name = "elevation" },
            ViewRequest.Parse("SW") with { Name = "axonometric" },
            ViewRequest.Parse(new Box(Plane.WorldXY, new Interval(-1500, 10500), new Interval(-1500, 8000), new Interval(-10, 1200))) with { Name = "section" },
        };
        var contexts = views.Select((v, i) => new ViewSettingsContext("selftest-" + i, v.Name, v.Kind, v.Cut is not null, 20000, 0.01)).ToArray();
        var settings = Settings.Resolve(contexts, views.Select(_ => new[] { "hidden=on", "tolerance=document" }).ToArray(), DocumentUnits.Millimetres);
        if (settings.HasErrors) throw new InvalidOperationException("Self-test settings did not resolve.");
        return views.Select((v, i) => v with { Settings = settings.Views[i] }).ToArray();
    }

    /// <summary>The fixture scene: plain solids, a mesh and a curve in millimetres, as owned inputs.</summary>
    internal static CaptureInput[] FixtureInputs()
    {
        var values = new object[]
        {
            new BoundingBox(new Point3d(0, 0, 0), new Point3d(4000, 3000, 2500)).ToBrep(),
            new Cylinder(new Circle(new Plane(new Point3d(6000, 1500, 0), Vector3d.ZAxis), 500), 3000).ToBrep(true, true),
            new Sphere(new Point3d(2000, 6000, 800), 800).ToBrep(),
            Mesh.CreateFromBox(new BoundingBox(new Point3d(8000, 0, 0), new Point3d(9000, 1000, 1000)), 2, 2, 2),
            new LineCurve(new Point3d(-1000, -1000, 0), new Point3d(10000, -1000, 0)),
        };
        return values.Select((v, i) => new CaptureInput(v, [0, i], "selftest/" + i)).ToArray();
    }

    /// <summary>Captures the fixture in a private headless document and draws it through
    /// <see cref="DrawingExecution.SolveBorrowedAsync"/>, the production dispatch.</summary>
    internal static DrawingBatch DrawFixture(CancellationToken token, IProgress<JobProgress>? progress = null, int? views = null)
    {
        using var document = RhinoDoc.CreateHeadless(null);
        document.ModelUnitSystem = UnitSystem.Millimeters;
        document.ModelAbsoluteTolerance = 0.01;
        document.ModelAngleToleranceRadians = RhinoMath.ToRadians(1);
        using var cache = new SceneCache();
        using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, FixtureInputs(), cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(50), token)) { }
        using var lease = capture.Accept();
        var requests = FixtureViews();
        if (views is int count) requests = requests.Take(count).ToArray();
        var identity = new RequestIdentity(lease.Value.Revision, string.Join("|", requests.Select(v => v.Revision)), Fixture, 1);
        // A worker-free in-process solve on every platform except where Windows dispatch picks its workers.
        return Task.Run(() => DrawingExecution.SolveBorrowedAsync(lease.Value, identity, requests, new(), token, progress)).GetAwaiter().GetResult();
    }

    /// <summary>The platform-comparable summary of one drawn view, in the frame's plane coordinates.</summary>
    public static DrawingSignature Signature(ViewDrawing drawing)
    {
        var plane = drawing.Frame.IsValid ? drawing.Frame.Plane : Plane.WorldXY;
        var classes = new Dictionary<string, ClassSignature>(StringComparer.Ordinal);
        foreach (var group in drawing.Items.GroupBy(p => p.Class).OrderBy(g => g.Key))
        {
            int pieces = 0, points = 0; double length = 0; var box = BoundingBox.Empty;
            foreach (var piece in group)
            {
                using var geometry = piece.CopyGeometry();
                if (geometry is Curve curve) { pieces++; length += curve.GetLength(); box.Union(curve.GetBoundingBox(plane)); }
                else if (geometry is global::Rhino.Geometry.Point point)
                {
                    points++; plane.ClosestParameter(point.Location, out var u, out var v); box.Union(new Point3d(u, v, 0));
                }
                else pieces++;
            }
            double R(double x) => Math.Round(x, 4);
            classes[group.Key.ToString()] = box.IsValid
                ? new(pieces, points, R(length), R(box.Min.X), R(box.Min.Y), R(box.Max.X), R(box.Max.Y))
                : new(pieces, points, R(length), 0, 0, 0, 0);
        }
        return new(drawing.Name, drawing.Error, Math.Round(drawing.Frame.Width, 4), Math.Round(drawing.Frame.Height, 4), classes,
            drawing.Regions.Count, drawing.Regions.Count(r => r.Region.Hatch is not null), Math.Round(drawing.Regions.Sum(r => Math.Abs(r.Region.Area)), 2));
    }

    internal static SignatureBaseline? EmbeddedBaseline()
    {
        using var stream = typeof(PlatformSelfTest).Assembly.GetManifestResourceStream(BaselineResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return SignatureBaseline.FromJson(reader.ReadToEnd());
    }

    /// <summary>A baseline from this host's drawing, for the Windows test that maintains the committed file.</summary>
    internal static SignatureBaseline Baseline(IReadOnlyList<DrawingSignature> signatures) => new(SignatureBaseline.CurrentSchema, Fixture,
        $"Rhino {RhinoApp.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}, .NET {Environment.Version}", signatures);

    /// <summary>Writes <paramref name="text"/> to Documents (or the temp folder) and returns the path.</summary>
    public static string Save(string text)
    {
        var name = "M2DPlus-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetTempPath() })
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                var path = Path.Combine(folder, name); File.WriteAllText(path, text); return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        throw new IOException("The self-test report could not be written to Documents or the temp folder.");
    }
}
