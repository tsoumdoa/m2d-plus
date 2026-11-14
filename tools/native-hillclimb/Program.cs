using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using Point = Rhino.Geometry.Point;

namespace NativeHillclimb;
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static void Write(string path, object value)
    { File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json)); File.Move(path + ".tmp", path, true); }
    [STAThread] private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (Console.ReadLine() != "go") return 2;
        var resolver = Assembly.LoadFrom(args[0]);
        resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
            .Invoke(null, new object[] { @"C:\Program Files\Rhino 8\System" });
        return Run(args);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Run(string[] args)
    {
        var start = Stopwatch.StartNew();
        var file = args[1]; var output = args[2]; var mode = args[3]; Directory.CreateDirectory(output);
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        if(mode=="replay") { HiddenReplay.Run(file,output); return 0; }
        if(mode=="probe") { HiddenProbe.Run(file,output); return 0; }
        if(mode=="audit") { HiddenAudit.Run(file,output); return 0; }
        var startup = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds; ScorerChecks.Run(); if (mode == "selftest") { Write(Path.Combine(output,"selftest.json"), new {passed=true}); return 0; } var captureTimer = Stopwatch.StartNew();
        Write(Path.Combine(output, "phase.json"), new { phase = "capture", file });
        using var doc = RhinoDoc.OpenHeadless(file);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(doc);
        using var capture = new SceneCapture(doc, watch, cache: cache);
        while (!capture.Step(TimeSpan.FromMilliseconds(12))) { }
        using var lease = capture.Accept(); var scene = lease.Value;
        var sources = scene.Prepare(default); var captureSeconds = captureTimer.Elapsed.TotalSeconds;
        var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.Static | BindingFlags.NonPublic)!;
        var views = new[] { ViewRequest.Parse("North elevation", doc), ViewRequest.Parse("SW axo", doc), ViewRequest.Parse("Top", doc), ViewRequest.Parse("Section long overlap", doc) };
        if (views[0].Error is not null) views[0] = ViewRequest.Parse("Back") with { Name = "North elevation" };
        if (views[1].Error is not null) views[1] = ViewRequest.Parse("SW") with { Name = "SW axo" };
        if (views[3].Error is not null) views[3] = ViewRequest.Parse(new Plane(new Point3d(scene.Bounds.Center.X, scene.Bounds.Center.Y, 0), Vector3d.XAxis, Vector3d.ZAxis)) with { Name = "Section long overlap" };
        Write(Path.Combine(output, "identity.json"), new { file, mode, startup, captureSeconds, rhino = RhinoApp.Version.ToString(), runtime = Environment.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), engineSha256 = Hash(typeof(Program).Assembly.Location), rhinoCommonSha256 = Hash(typeof(Curve).Assembly.Location), sourceCount = sources.Count, scene.Cost, productAdmission = scene.Cost.Admitted(1, NativeScheduler.Global.BudgetBytes), note = "Same current SolveView, isolated owned worker; bypass 512 MiB in-process admission only, hard external 16 GiB cap. No product route altered." });
        foreach (var hidden in new[] { true, false }) for (var vi = 0; vi < views.Length; vi++)
        {
            var view = views[vi]; var key = $"v{vi}-h{hidden}";
            var context = new ViewSettingsContext(key, view.Name, view.Kind, view.Cut is not null, scene.Bounds.Diagonal.Length, scene.Tolerance, view.Cut?.SourceDepth);
            var settings = Settings.Resolve(new[] { context }, new[] { new[] { $"hidden={(hidden ? "on" : "off")}", "farclip=none", "beyond=off", "tangents=off", "seams=off" } }, DocumentUnits.Metres);
            if (settings.HasErrors) throw new InvalidOperationException(string.Join(";", settings.Diagnostics));
            view = view with { Settings = settings.Views[0] };
            var rows = new List<object>(); var baselineTimes = new List<double>(); var candidateTimes = new List<double>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                Write(Path.Combine(output, "phase.json"), new { phase = "baseline", key, repeat });
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));
                    var timer = Stopwatch.StartNew();
                    using var baseline = (ViewDrawing)solve.Invoke(null, new object[] { scene, sources, view, new NativeSettings(false), cts.Token })!;
                    Locus.Roundtrip(baseline, Path.Combine(output, key + $"-r{repeat}-baseline.3dm"));
                    var seconds = timer.Elapsed.TotalSeconds; baselineTimes.Add(seconds);
                    if (mode == "baseline")
                    { rows.Add(new { repeat, seconds, baseline.Error, baseline.Report, count = baseline.Items.Count }); }
                    else
                    {
                        Write(Path.Combine(output, "phase.json"), new { phase = "candidate", key, repeat });
                        using var input = Experiments.Prepare(mode, sources, scene, view);
                        using var candidateCts = new CancellationTokenSource(TimeSpan.FromSeconds(600));
                        timer.Restart();
                        // Include prepass, canonicalisation and output ownership in candidate timing.
                        using var candidate = (ViewDrawing)solve.Invoke(null, new object[] { scene, input.Sources, view, new NativeSettings(mode == "threads"), candidateCts.Token })!;
                        var canonicalSeconds = mode == "threads" ? Locus.Canonicalize(candidate) : 0;
                        Locus.Roundtrip(candidate, Path.Combine(output, key + $"-r{repeat}-candidate.3dm"));
                        var candidateSeconds = timer.Elapsed.TotalSeconds + input.Seconds; candidateTimes.Add(candidateSeconds);
                        var check = Locus.Compare(baseline, candidate, scene.Tolerance);
                        rows.Add(new { repeat, baselineSeconds = seconds, candidateSeconds, input.Removed, input.Changed, input.Seconds, canonicalSeconds, check, baselineError = baseline.Error, candidateError = candidate.Error, baselineReport = baseline.Report, candidateReport = candidate.Report });
                        Write(Path.Combine(output, key + $"-r{repeat}-disagreements.json"), check);
                    }
                    Write(Path.Combine(output, key + ".json"), new { key, view.Name, hidden, mode, tolerance = scene.Tolerance, baselineTimes, candidateTimes, rows });
                    if (baseline.Error is not null || (seconds >= 10 && (candidateTimes.Count == 0 || candidateTimes[^1] >= 10))) break;
                }
                catch (Exception e)
                {
                    rows.Add(new { repeat, error = e.InnerException?.ToString() ?? e.ToString() });
                    Write(Path.Combine(output, key + ".json"), new { key, view.Name, hidden, mode, tolerance = scene.Tolerance, baselineTimes, candidateTimes, rows }); break;
                }
            }
            Console.WriteLine($"{Path.GetFileName(file)} {key} {mode}: baseline={string.Join(',', baselineTimes)} candidate={string.Join(',', candidateTimes)}");
        }
        Write(Path.Combine(output, "phase.json"), new { phase = "complete" }); return 0;
    }
    internal static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }
}


