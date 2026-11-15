using System.Collections.Concurrent;
using System.Text.Json;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

/// <summary>
/// Audit: does the definition-local verdict (what production uses) agree with validating each instance's own
/// world-space copy (what main does)? Every disagreement is listed; none is hidden or averaged away.
/// </summary>
internal static class Verdicts
{
    internal static void Audit(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, string stem, string output)
    {
        var tolerance = scene.Tolerance;
        var bodies = prepared.Where(p => p.Geometry is Brep or Mesh).ToArray();
        var world = new string[bodies.Length]; var local = new string?[bodies.Length];
        var canonical = new ConcurrentDictionary<string, Lazy<string>>(StringComparer.Ordinal);
        // PERFF_AUDIT_THREADS=1 gives the serial reference; world verdicts must not depend on the thread count.
        var threads = int.TryParse(Environment.GetEnvironmentVariable("PERFF_AUDIT_THREADS"), out var requested) ? requested : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, bodies.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            try { using var copy = SectionGeometry.Convert(bodies[i].Geometry, tolerance); world[i] = ""; }
            catch (Exception ex) when (ex.GetType().Name == "SectionFailure") { world[i] = ex.Message; }
            if (bodies[i].Canonical is not { } shape) return;
            // The production path first runs the cheap conversion checks on the instance itself.
            try { using var copy = SectionGeometry.Convert(bodies[i].Geometry, tolerance, validate: false); }
            catch (Exception ex) when (ex.GetType().Name == "SectionFailure") { local[i] = ex.Message; return; }
            local[i] = canonical.GetOrAdd(shape.Key, _ => new Lazy<string>(() => SectionGeometry.CanonicalVerdict(shape, tolerance))).Value;
        });
        var keyed = Enumerable.Range(0, bodies.Length).Where(i => local[i] is not null).ToArray();
        // Compare the verdict (valid or not). Message text may name a point, which is world space in one and local in the other.
        var disagreements = keyed.Where(i => (world[i].Length == 0) != (local[i]!.Length == 0)).ToArray();
        var report = new
        {
            model = stem, threads, tolerance, bodies = bodies.Length, keyed = keyed.Length, unkeyed = bodies.Length - keyed.Length, distinctKeys = canonical.Count,
            worldInvalid = world.Count(w => w.Length != 0), localInvalid = keyed.Count(i => local[i]!.Length != 0),
            scaledKeys = canonical.Keys.Count(k => k.Contains("|scale=", StringComparison.Ordinal)),
            maxAbsCoordinate = scene.Bounds.IsValid ? Math.Max(scene.Bounds.Min.MaximumCoordinate, scene.Bounds.Max.MaximumCoordinate) : 0,
            seconds = timer.Elapsed.TotalSeconds, disagreements = disagreements.Length,
            worldInvalidIdentities = Enumerable.Range(0, bodies.Length).Where(i => world[i].Length != 0 && (local[i] is null || local[i]!.Length == 0)).Select(i => bodies[i].Identity).ToArray(),
            cases = disagreements.Take(50).Select(i => new { bodies[i].Identity, key = bodies[i].Canonical!.Key, scale = bodies[i].Canonical!.Scale, world = world[i], local = local[i] }).ToArray(),
            invalidMessages = keyed.Where(i => local[i]!.Length != 0).GroupBy(i => local[i]!).ToDictionary(g => g.Key, g => g.Count())
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, stem + $"-verdict-audit-t{threads}.json"), json);
        Console.WriteLine(json);
    }

    /// <summary>
    /// Is each geometry step the section engine runs per source the same when other threads are running Rhino
    /// geometry at the same time? A serial pass gives the reference signature for every (body, step); then each step
    /// runs alone on N threads, and finally all steps run mixed on N threads. Any signature that differs is counted.
    /// </summary>
    internal static void Stress(PreparedScene scene, IReadOnlyList<PreparedLeaf> prepared, string stem, string output)
    {
        var tolerance = scene.Tolerance;
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("PERFF_STRESS_ROUNDS"), out var r) ? r : 6;
        var threads = int.TryParse(Environment.GetEnvironmentVariable("PERFF_AUDIT_THREADS"), out var t) ? t : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var limit = int.TryParse(Environment.GetEnvironmentVariable("PERFF_STRESS_BODIES"), out var b) ? b : 600;
        var bodies = prepared.Where(p => p.Geometry is Brep).Take(limit).Select(p => SectionGeometry.Convert(p.Geometry, tolerance, validate: false)).ToArray();
        static string N(double v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        SectionRequest Cut(Brep brep) { var box = brep.GetBoundingBox(true); return new(new Plane(box.Center, Vector3d.YAxis, Vector3d.ZAxis), tolerance, .001); }
        var heavy = Math.Max(1, rounds / 6);
        var steps = new (string Name, bool CurveCurve, int Rounds, Func<Brep, string> Run)[]
        {
            ("validate (product check: BrepBrep + interval attribution)", true, heavy, brep => { try { SectionGeometry.ValidateFaceIntersections(brep, tolerance); return "valid"; } catch (Exception ex) when (ex.GetType().Name == "SectionFailure") { return ex.Message; } }),
            ("interval attribution (EdgeProvenance.Split, product: gated)", false, rounds, brep =>
            {
                var originals = brep.Edges.Select(e => e.DuplicateCurve()).ToArray(); var text = new System.Text.StringBuilder();
                try
                {
                    foreach (var edge in brep.Edges)
                    {
                        var parts = EdgeProvenance.Split(edge, originals.Select(c => (c, EdgeOrigin.Original)), tolerance);
                        text.Append($"{parts.Count}:{string.Join("", parts.Select(x => (int)x.Origin))}:{N(parts.Sum(x => x.Curve.GetLength()))};");
                        foreach (var part in parts) part.Dispose();
                    }
                }
                finally { foreach (var c in originals) c.Dispose(); }
                return text.ToString();
            }),
            ("raw CurveCurve overlaps, no gate (control)", true, rounds, brep =>
            {
                var text = new System.Text.StringBuilder();
                foreach (var edge in brep.Edges)
                foreach (var other in brep.Edges)
                {
                    if (!BoundingBox.Intersection(edge.GetBoundingBox(true), other.GetBoundingBox(true)).IsValid) continue;
                    using var events = global::Rhino.Geometry.Intersect.Intersection.CurveCurve(edge, other, tolerance, tolerance);
                    text.Append($"{events.Count(e => e.IsOverlap)}:{N(events.Where(e => e.IsOverlap).Sum(e => e.OverlapA.Length / edge.Domain.Length))};");
                }
                return text.ToString();
            }),
            ("BrepBrep face pairs only", false, heavy, brep =>
            {
                var text = new System.Text.StringBuilder();
                for (var i = 0; i < brep.Faces.Count; i++)
                for (var j = i + 1; j < brep.Faces.Count; j++)
                {
                    using var first = brep.Faces[i].DuplicateFace(false); using var second = brep.Faces[j].DuplicateFace(false);
                    var ok = global::Rhino.Geometry.Intersect.Intersection.BrepBrep(first, second, tolerance, out var curves, out var points);
                    text.Append($"{i}/{j}:{ok}:{curves.Length}:{N(curves.Sum(c => c.GetLength()))}:{points.Length};");
                    foreach (var curve in curves) curve.Dispose();
                }
                return text.ToString();
            }),
            ("convert + material components", false, rounds, brep => { using var copy = SectionGeometry.Convert(brep, tolerance, validate: false); var parts = SectionGeometry.MaterialComponents(copy, tolerance); try { return $"{parts.Count}:{parts.Sum(x => x.Faces.Count)}:{copy.IsSolid}"; } finally { foreach (var x in parts) x.Dispose(); } }),
            ("classify + certificate", false, rounds, brep => { var c = SectionGeometry.Classify("s", 0, brep, Cut(brep), out var evidence); using (evidence) return $"{c.Status}:{c.Certificate.Length}:{evidence?.Loops.Length}:{N(evidence?.Loops.Sum(l => l.GetLength()) ?? 0)}"; }),
            ("plane intersection + join + planar regions", false, rounds, brep =>
            {
                var request = Cut(brep); var loops = SectionGeometry.Intersections(brep, request.Plane, tolerance);
                try
                {
                    var text = $"{loops.Length}:{N(loops.Sum(l => l.GetLength()))}:{loops.Count(l => l.IsClosed)}";
                    if (!brep.IsSolid || loops.Length == 0) return text;
                    try { var regions = SectionGeometry.PlanarRegions(loops, request); try { return text + $":{regions.Count}:{N(regions.Sum(SectionGeometry.Area))}"; } finally { foreach (var x in regions) x.Dispose(); } }
                    catch (Exception ex) when (ex.GetType().Name == "SectionFailure") { return text + ":failure:" + ex.Message; }
                }
                finally { foreach (var l in loops) l.Dispose(); }
            }),
            ("trim + cap + join (Halfspace)", false, rounds, brep =>
            {
                var diagnostics = new List<SectionDiagnostic>();
                try
                {
                    var pieces = PhysicalSectionClipper.Halfspace("s", brep, Cut(brep), diagnostics);
                    try { return $"{pieces.Count}:{diagnostics.Count}:{pieces.Sum(p => p.Faces.Count)}:{pieces.Sum(p => p.Edges.Count)}:{N(pieces.Sum(p => p.IsSolid ? Math.Abs(p.GetVolume()) : 0))}"; }
                    finally { foreach (var p in pieces) p.Dispose(); }
                }
                catch (Exception ex) when (ex.GetType().Name == "SectionFailure") { return "failure:" + ex.Message; }
            }),
        };
        var reference = steps.Select(step => bodies.Select(step.Run).ToArray()).ToArray();
        // Serial repeat: the reference itself must be reproducible, or nothing below means anything.
        var serialRepeat = steps.Select((step, k) => bodies.Select(step.Run).Where((value, i) => value != reference[k][i]).Count()).ToArray();
        var alone = new int[steps.Length]; var mixedAll = new int[steps.Length]; var mixedSafe = new int[steps.Length]; var examples = new ConcurrentBag<string>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        void Check(int k, int i, int[] counter, string mode)
        {
            var value = steps[k].Run(bodies[i]);
            if (value == reference[k][i]) return;
            Interlocked.Increment(ref counter[k]); if (examples.Count < 60) examples.Add($"{mode}/{steps[k].Name}/body {i}: serial [{reference[k][i]}] parallel [{value}]");
        }
        Console.WriteLine("serial reference repeat mismatches: " + string.Join(", ", serialRepeat));
        for (var k = 0; k < steps.Length; k++)
        {
            for (var round = 0; round < steps[k].Rounds; round++)
                Parallel.For(0, bodies.Length, options, i => Check(k, i, alone, "alone"));
            Console.WriteLine($"alone: {steps[k].Name}: {alone[k]} mismatches in {(long)steps[k].Rounds * bodies.Length} runs");
        }
        // Mixed, every light step together (the interval attribution included).
        var light = Enumerable.Range(0, steps.Length).Where(k => steps[k].Rounds == rounds).ToArray();
        for (var round = 0; round < rounds; round++)
            Parallel.For(0, bodies.Length * light.Length, options, n => Check(light[n % light.Length], n / light.Length, mixedAll, "mixed-all"));
        // Mixed, without anything that relies on curve-curve overlap: the candidate parallel phase.
        var safe = light.Where(k => !steps[k].CurveCurve).ToArray();
        for (var round = 0; round < rounds; round++)
            Parallel.For(0, bodies.Length * safe.Length, options, n => Check(safe[n % safe.Length], n / safe.Length, mixedSafe, "mixed-no-curve-curve"));
        var report = new
        {
            model = stem, bodies = bodies.Length, threads,
            steps = steps.Select((step, k) => new { step.Name, usesCurveCurve = step.CurveCurve, serialRepeatMismatches = serialRepeat[k],
                aloneRuns = (long)step.Rounds * bodies.Length, aloneMismatches = alone[k],
                mixedAllRuns = light.Contains(k) ? (long)rounds * bodies.Length : 0, mixedAllMismatches = mixedAll[k],
                mixedNoCurveCurveRuns = safe.Contains(k) ? (long)rounds * bodies.Length : 0, mixedNoCurveCurveMismatches = mixedSafe[k] }).ToArray(),
            examples = examples.ToArray()
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, stem + $"-parallel-stress-t{threads}.json"), json);
        Console.WriteLine(json);
        foreach (var body in bodies) body.Dispose();
    }
}
