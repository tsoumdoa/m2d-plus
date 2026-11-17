// da:<view> — the product's dense native audit (mesh tier, step C), off and on, back to back on one prepared scene.
// Reports wall time, class lengths, the audit's own Info line, and a direct ray check of the stretches it moved.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace M2DPlus.PerfF;

internal static class DenseAuditCheck
{
    private static ViewDrawing Solve(PreparedScene scene, IReadOnlyList<PreparedLeaf> sources, ViewRequest view, NativeSettings settings, double timeout)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var solve = typeof(NativeDrawing).GetMethod("SolveView", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ViewDrawing)solve.Invoke(null, new object[] { scene, sources, view, settings, limit.Token })!;
    }

    private static double[] Lengths(ViewDrawing drawing)
    {
        var length = new double[3];
        foreach (var piece in drawing.Items)
        {
            using var g = piece.CopyGeometry(); if (g is not Curve curve) continue;
            length[piece.Class == LineClass.Visible ? 0 : piece.Class == LineClass.Hidden ? 1 : 2] += curve.GetLength();
        }
        return length;
    }

    internal static void Run(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, ViewRequest view, string stem, string output, string[] variants, double timeout)
    {
        double tol = scene.Tolerance; var invariant = CultureInfo.InvariantCulture;
        var options = new DenseAuditOptions();
        if (Environment.GetEnvironmentVariable("PERFF_DA_BAND") is { Length: > 0 } band) options = options with { BandTolerances = double.Parse(band, invariant) };
        if (Environment.GetEnvironmentVariable("PERFF_DA_TRIANGLES") is { Length: > 0 } triangles) options = options with { TileTriangles = long.Parse(triangles, invariant) };
        int samples = 600; foreach (var v in variants) if (v.StartsWith("samples:", StringComparison.Ordinal)) samples = int.Parse(v[8..], invariant);
        var off = new NativeSettings { DenseAudit = false }; var spans = new List<Curve>();
        var on = new NativeSettings { DenseAudit = true, DenseAuditOptions = options };
        (ViewDrawing Drawing, double Wall) Timed(NativeSettings settings) { var timer = Stopwatch.StartNew(); var d = Solve(scene, prepared, view, settings, timeout); return (d, timer.Elapsed.TotalSeconds); }

        // quick: no cold solve and no repeats (for views where one native solve takes minutes).
        bool quick = variants.Contains("quick");
        if (!quick) using (Solve(scene, prepared, view, off, timeout)) { }                        // cold
        var (plain, plainWall) = Timed(off);
        long builds = CertifiedMesh.Builds;
        var (first, firstWall) = Timed(on with { ObserveDenseAuditSpan = c => spans.Add(c.DuplicateCurve()) });
        builds = CertifiedMesh.Builds - builds;
        var (second, secondWall) = quick ? (first, firstWall) : Timed(on);
        var (again, againWall) = quick ? (plain, plainWall) : Timed(off);
        try
        {
            double[] before = Lengths(plain), after = Lengths(second);
            string info = first.Diagnostics.FirstOrDefault(d => d.StartsWith("Dense native audit", StringComparison.Ordinal)) ?? "(no audit line)";
            double moved = spans.Sum(s => s.GetLength());
            Console.WriteLine($"=== {stem} dense audit: off {plainWall:F2}s / {againWall:F2}s, on first {firstWall:F2}s (meshes built {builds}), on again {secondWall:F2}s; added {secondWall - Math.Min(plainWall, againWall):F2}s; route {second.Report.Route}; native calls {second.NativeCalls}; error {second.Error}");
            Console.WriteLine($"    off: Visible {before[0]:F1} Hidden {before[1]:F1} Unresolved {before[2]:F1} | on: Visible {after[0]:F1} Hidden {after[1]:F1} Unresolved {after[2]:F1}; moved world length {moved:F2} in {spans.Count} spans; phase {second.Phases.Seconds.GetValueOrDefault("dense native audit"):F2}s (meshes and index {second.Phases.Seconds.GetValueOrDefault("dense native audit meshes"):F2}s; first run {first.Phases.Seconds.GetValueOrDefault("dense native audit"):F2}s, {first.Phases.Seconds.GetValueOrDefault("dense native audit meshes"):F2}s); native solve {second.Phases.Seconds.GetValueOrDefault("native solve"):F2}s");
            Console.WriteLine($"    {info}");
            Console.WriteLine($"    mesh build s: meshing {CertifiedMesh.BuildSeconds[0]:F2}, certificates {CertifiedMesh.BuildSeconds[1]:F2}, vertex parameters {CertifiedMesh.BuildSeconds[2]:F2}, triangle bounds {CertifiedMesh.BuildSeconds[3]:F2}, measuring triangles with no bound {CertifiedMesh.BuildSeconds[4]:F2}; vertices pulled back by closest point {CertifiedMesh.PulledBack}");

            // ---- Direct ray check of the moved stretches: is something really more than a tolerance in front?
            var plane = second.Frame.Plane; var e = plane.ZAxis; var xy = Transform.PlaneToPlane(plane, Plane.WorldXY);
            var bodies = new List<(GeometryBase Geometry, BoundingBox Box, bool Owned)>();
            foreach (var leaf in prepared)
            {
                GeometryBase? g = leaf.Geometry switch { Brep b => b, Mesh m => m, Extrusion x => x.ToBrep(), Surface s => s.ToBrep(), _ => null };
                if (g is null) continue;
                bodies.Add((g, g.GetBoundingBox(xy), !ReferenceEquals(g, leaf.Geometry)));
            }
            double far = bodies.Count == 0 ? 0 : bodies.Max(b => b.Box.Max.Z);
            int checkedPoints = 0, hidden = 0, thin = 0, clear = 0, failed = 0; var clearNotes = new List<string>();
            var points = new List<Point3d>();
            foreach (var span in spans) foreach (double k in new[] { .5, .2, .8 }) points.Add(span.PointAt(span.Domain.ParameterAt(k)));
            int stride = Math.Max(1, points.Count / Math.Max(1, samples));
            for (int i = 0; i < points.Count; i += stride)
            {
                var point = points[i]; var local = point; local.Transform(xy); double best = double.NegativeInfinity; bool unsure = false;
                foreach (var (geometry, box, _) in bodies)
                {
                    if (local.X < box.Min.X - 8 * tol || local.X > box.Max.X + 8 * tol || local.Y < box.Min.Y - 8 * tol || local.Y > box.Max.Y + 8 * tol || box.Max.Z < local.Z + tol) continue;
                    if (geometry is Mesh mesh)
                    {
                        double d = Intersection.MeshRay(mesh, new Ray3d(point + e * tol, e)); if (d >= 0) best = Math.Max(best, d + tol);
                        continue;
                    }
                    using var ray = new LineCurve(point + e * tol, point + e * (far - local.Z + 1));
                    if (!Intersection.CurveBrep(ray, (Brep)geometry, tol / 10, out var overlaps, out var hits)) { unsure = true; continue; }
                    foreach (var h in hits) best = Math.Max(best, (h - point) * e);
                    foreach (var o in overlaps) { best = Math.Max(best, Math.Max((o.PointAtStart - point) * e, (o.PointAtEnd - point) * e)); o.Dispose(); }
                }
                checkedPoints++;
                if (best > 4 * tol) hidden++; else if (best > tol) thin++; else if (unsure) failed++;
                else { clear++; if (clearNotes.Count < 8) clearNotes.Add(FormattableString.Invariant($"({point.X:F4},{point.Y:F4},{point.Z:F4})")); }
            }
            Console.WriteLine($"    ray check of moved stretches: {checkedPoints} points: cover over 4 tol {hidden}, cover 1 to 4 tol {thin}, nothing in front {clear}, intersection failed {failed}{(clearNotes.Count > 0 ? "; nothing in front at " + string.Join(" ", clearNotes) : "")}");
            // ---- Witness points from elsewhere (world points native draws Visible and a ray check found Hidden): how many lie on a moved stretch?
            int witnesses = 0, covered = 0;
            if (Environment.GetEnvironmentVariable("PERFF_DA_WITNESSES") is { Length: > 0 } path)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var w in document.RootElement.GetProperty("witnesses").EnumerateArray())
                {
                    var p = w.GetProperty("point"); var point = new Point3d(p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble()); witnesses++;
                    foreach (var span in spans) if (span.ClosestPoint(point, out double t, 10 * tol)) { covered++; break; }
                }
                Console.WriteLine($"    witnesses from {Path.GetFileName(path)}: {witnesses}, on a moved stretch (within 10 tol) {covered}");
            }
            File.WriteAllText(Path.Combine(output, stem + "-da.json"), JsonSerializer.Serialize(new
            {
                view = view.Name, tolerance = tol, plainWall, againWall, firstWall, secondWall, added = secondWall - Math.Min(plainWall, againWall), builds,
                route = second.Report.Route, nativeCalls = second.NativeCalls, before, after, moved, spans = spans.Count, info,
                phase = second.Phases.Seconds.GetValueOrDefault("dense native audit"), meshPhase = second.Phases.Seconds.GetValueOrDefault("dense native audit meshes"), firstPhase = first.Phases.Seconds.GetValueOrDefault("dense native audit"), firstMeshPhase = first.Phases.Seconds.GetValueOrDefault("dense native audit meshes"), quick, nativeSolve = second.Phases.Seconds.GetValueOrDefault("native solve"),
                check = new { points = checkedPoints, hidden, thin, clear, failed, clearNotes }, witnesses, covered, options
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (geometry, _, owned) in bodies) if (owned) geometry.Dispose();
        }
        finally { plain.Dispose(); first.Dispose(); if (!quick) { second.Dispose(); again.Dispose(); } }
        foreach (var span in spans) span.Dispose();
    }

    /// <summary>
    /// dp:<view> — the same audit through the production worker pool (one worker): off, on, on again. Reports wall time,
    /// class lengths, the worker's cap, its peak private bytes while each solve ran (sampled every 50 ms) and the Job
    /// Object's peak commit. `check` also solves in this process with the audit on and compares the two drawings' bytes.
    /// </summary>
    internal static void RunPool(PreparedScene scene, ViewRequest view, string stem, string output, string[] variants, string resolver)
    {
        var invariant = CultureInfo.InvariantCulture; var options = new DenseAuditOptions();
        if (Environment.GetEnvironmentVariable("PERFF_DA_TRIANGLES") is { Length: > 0 } triangles) options = options with { TileTriangles = long.Parse(triangles, invariant) };
        string configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/M2DPlus.Worker/bin", configuration, "net" + Environment.Version.Major + ".0-windows"));
        using var pool = new DrawingWorkerPool(new WorkerOptions { Count = 1, Directory = directory, MemoryBytes = long.Parse(Environment.GetEnvironmentVariable("PERFF_DP_CAP_MIB") ?? "0", invariant) * 1048576, IdleTimeout = TimeSpan.FromMinutes(30), SolveTimeout = TimeSpan.FromHours(2) });
        if (!pool.Installed) throw new InvalidOperationException("No worker at " + directory + ": build src/M2DPlus.Worker in the same configuration first.");
        long generation = 0; var rows = new List<object>(); byte[]? pooled = null;
        (double Wall, double[] Lengths, long Sampled, long JobPeak, string Info, double Phase) Pooled(bool audit, string label)
        {
            long peak = 0; using var stop = new CancellationTokenSource();
            var sampler = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    long sum = 0;
                    foreach (int pid in pool.ProcessIds) { try { using var process = Process.GetProcessById(pid); sum += process.PrivateMemorySize64; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                    peak = Math.Max(peak, sum); Thread.Sleep(50);
                }
            }) { IsBackground = true };
            sampler.Start(); var timer = Stopwatch.StartNew();
            using var batch = pool.SolveAsync(scene, new RequestIdentity(scene.Revision, "views", label, ++generation), [view], new NativeSettings { DenseAudit = audit, DenseAuditOptions = options }, default).GetAwaiter().GetResult();
            double wall = timer.Elapsed.TotalSeconds; stop.Cancel(); sampler.Join();
            var drawing = batch.Views[0]; if (audit) pooled = WorkerCodec.CaptureDrawing(drawing, true);
            string info = drawing.Diagnostics.FirstOrDefault(d => d.StartsWith("Dense native audit", StringComparison.Ordinal)) ?? "";
            var result = (wall, Lengths(drawing), peak, pool.PeakBytes, info, drawing.Phases.Seconds.GetValueOrDefault("dense native audit"));
            Console.WriteLine($"    pool {label}: {wall:F2}s; Visible {result.Item2[0]:F1} Hidden {result.Item2[1]:F1} Unresolved {result.Item2[2]:F1}; worker private bytes peak {peak / 1048576.0:F0} MiB; job peak commit {pool.PeakBytes / 1048576.0:F0} MiB; cap {pool.LastSizing?.CapBytes / 1048576.0:F0} MiB; audit phase {result.Item6:F2}s; error {drawing.Error}");
            if (info.Length > 0) Console.WriteLine("    " + info);
            rows.Add(new { label, wall, lengths = result.Item2, sampledPeak = peak, jobPeak = pool.PeakBytes, cap = pool.LastSizing?.CapBytes, info, phase = result.Item6, error = drawing.Error });
            return result;
        }
        Console.WriteLine($"=== {stem} dense audit through one worker, runtime {Environment.Version}, worker {directory}");
        var machine = WorkerMachineMemory.Read();
        Console.WriteLine($"    pool sizing: scene needs a cap of {WorkerMemoryPolicy.Required(scene.Cost) / 1048576.0:F0} MiB; machine {machine.PhysicalBytes / 1048576.0:F0} MiB physical, {machine.AvailableBytes / 1048576.0:F0} MiB available; budget {WorkerMemoryPolicy.Budget(machine) / 1048576.0:F0} MiB");
        var off = Pooled(false, "off"); var first = Pooled(true, "on first"); var again = Pooled(true, "on again");
        bool? identical = null;
        if (variants.Contains("check"))
        {
            using var local = Solve(scene, scene.Prepare(CancellationToken.None), view, new NativeSettings { DenseAudit = true, DenseAuditOptions = options }, 3600);
            identical = WorkerCodec.CaptureDrawing(local, true).AsSpan().SequenceEqual(pooled);
            Console.WriteLine($"    in-process drawing with the audit on is byte-identical to the worker's: {identical}");
        }
        File.WriteAllText(Path.Combine(output, stem + "-dp.json"), JsonSerializer.Serialize(new { view = view.Name, runtime = Environment.Version.ToString(), rows, identical, options }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
