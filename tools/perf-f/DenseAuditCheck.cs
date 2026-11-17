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
        if (Environment.GetEnvironmentVariable("PERFF_DA_TRIANGLES") is { Length: > 0 } triangles) options = options with { MaxTriangles = long.Parse(triangles, invariant) };
        int samples = 600; foreach (var v in variants) if (v.StartsWith("samples:", StringComparison.Ordinal)) samples = int.Parse(v[8..], invariant);
        var off = new NativeSettings { DenseAudit = false }; var spans = new List<Curve>();
        var on = new NativeSettings { DenseAudit = true, DenseAuditOptions = options };
        (ViewDrawing Drawing, double Wall) Timed(NativeSettings settings) { var timer = Stopwatch.StartNew(); var d = Solve(scene, prepared, view, settings, timeout); return (d, timer.Elapsed.TotalSeconds); }

        using (Solve(scene, prepared, view, off, timeout)) { }                                    // cold
        var (plain, plainWall) = Timed(off);
        long builds = CertifiedMesh.Builds;
        var (first, firstWall) = Timed(on with { ObserveDenseAuditSpan = c => spans.Add(c.DuplicateCurve()) });
        builds = CertifiedMesh.Builds - builds;
        var (second, secondWall) = Timed(on);
        var (again, againWall) = Timed(off);
        using (plain) using (first) using (second) using (again)
        {
            double[] before = Lengths(plain), after = Lengths(second);
            string info = first.Diagnostics.FirstOrDefault(d => d.StartsWith("Dense native audit", StringComparison.Ordinal)) ?? "(no audit line)";
            double moved = spans.Sum(s => s.GetLength());
            Console.WriteLine($"=== {stem} dense audit: off {plainWall:F2}s / {againWall:F2}s, on first {firstWall:F2}s (meshes built {builds}), on again {secondWall:F2}s; added {secondWall - Math.Min(plainWall, againWall):F2}s; route {second.Report.Route}; native calls {second.NativeCalls}; error {second.Error}");
            Console.WriteLine($"    off: Visible {before[0]:F1} Hidden {before[1]:F1} Unresolved {before[2]:F1} | on: Visible {after[0]:F1} Hidden {after[1]:F1} Unresolved {after[2]:F1}; moved world length {moved:F2} in {spans.Count} spans; phase {second.Phases.Seconds.GetValueOrDefault("dense native audit"):F2}s; native solve {second.Phases.Seconds.GetValueOrDefault("native solve"):F2}s");
            Console.WriteLine($"    {info}");

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
                phase = second.Phases.Seconds.GetValueOrDefault("dense native audit"), nativeSolve = second.Phases.Seconds.GetValueOrDefault("native solve"),
                check = new { points = checkedPoints, hidden, thin, clear, failed, clearNotes }, witnesses, covered, options
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (geometry, _, owned) in bodies) if (owned) geometry.Dispose();
        }
        foreach (var span in spans) span.Dispose();
    }
}
