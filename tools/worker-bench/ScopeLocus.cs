using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

// Research-only cross-build archive comparison. Production fingerprint checks stay strict.
internal static class ScopeLocus
{
    private sealed record Coverage(long Samples, long Misses, long WrongClass, long WrongProfile, long WrongSolvedClass,
        long ParameterFallbacks, long InvalidSamples, object[] Examples);
    internal static void Compare(string main, string branch, string output)
    {
        Directory.CreateDirectory(output);
        using var core = new Rhino.Runtime.InProcess.RhinoCore(["/nosplash", "/notemplate"], Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        var rows = new List<object>(); var failures = 0;
        foreach (var path in Directory.GetFiles(branch, "drawing-0-0.json", SearchOption.AllDirectories).Order())
        {
            var relative = Path.GetRelativePath(branch, path);
            var left = WorkerCodec.Decode<WorkerCodec.Drawing>(File.ReadAllBytes(Path.Combine(main, relative)));
            var right = WorkerCodec.Decode<WorkerCodec.Drawing>(File.ReadAllBytes(path));
            if (left.Fingerprint.RhinoBuild != right.Fingerprint.RhinoBuild || left.Fingerprint.RuntimeVersion != right.Fingerprint.RuntimeVersion ||
                left.Fingerprint.RhinoCommonSha256 != right.Fingerprint.RhinoCommonSha256 || left.Schema != 4 || right.Schema != 4)
                throw new InvalidOperationException("Cross-build environment mismatch: " + relative);
            // Exact geometry/class/region/frame identity is a stronger control than sampled coverage.
            // Exclude run timings and build fingerprints, as the existing identity archives do.
            var exactIdentity = Identity(left).SequenceEqual(Identity(right));
            using var a = exactIdentity ? null : WorkerCodec.RestoreDrawing(WorkerCodec.Encode(left with { Fingerprint = WorkerCodec.Fingerprint() }));
            using var b = exactIdentity ? null : WorkerCodec.RestoreDrawing(WorkerCodec.Encode(right with { Fingerprint = WorkerCodec.Fingerprint() }));
            var tolerance = Math.Max(left.Values.Tolerance, right.Values.Tolerance) * 1.25;
            var spacing = Math.Max(.01, Math.Max(left.Values.Tolerance, right.Values.Tolerance) * 10);
            var missing = exactIdentity ? new Coverage(0, 0, 0, 0, 0, 0, 0, []) : Sample(a!, b!, tolerance, spacing);
            var extra = exactIdentity ? new Coverage(0, 0, 0, 0, 0, 0, 0, []) : Sample(b!, a!, tolerance, spacing);
            var regionsEqual = WorkerCodec.Encode(left.Regions).SequenceEqual(WorkerCodec.Encode(right.Regions));
            var frameEqual = left.Values.Frame.Equals(right.Values.Frame);
            var pass = left.Values.Error is null && right.Values.Error is null && missing.Misses == 0 && extra.Misses == 0 && regionsEqual && frameEqual &&
                left.Values.Report.Quarantined == right.Values.Report.Quarantined && left.Values.RoutingQuarantine == right.Values.RoutingQuarantine;
            if (!pass) failures++;
            var row = new { relative, pass, exactIdentity, tolerance, spacing, missing, extra, regionsEqual, frameEqual,
                regions = left.Regions.Length, piecesMain = left.Pieces.Length, piecesBranch = right.Pieces.Length, mainFingerprint = left.Fingerprint, branchFingerprint = right.Fingerprint };
            rows.Add(row);
            var resultPath = Path.Combine(output, relative.Replace("drawing-0-0.json", "locus.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!); File.WriteAllBytes(resultPath, WorkerCodec.Encode(row));
            Console.WriteLine($"{relative}: pass={pass}; missing={missing.Misses}; extra={extra.Misses}; regions={regionsEqual}");
        }
        File.WriteAllBytes(Path.Combine(output, "summary.json"), WorkerCodec.Encode(new { cases = rows.Count, failures, rows }));
        if (failures > 0) throw new InvalidOperationException($"{failures} locus/region/frame comparisons failed; see evidence.");
    }
    private static byte[] Identity(WorkerCodec.Drawing dto) => WorkerCodec.Encode(new
    { dto.Pieces, dto.Regions, dto.Values.Frame, dto.Values.Error, dto.Values.RoutingQuarantine, dto.Values.Report.Quarantined });

    private static Coverage Sample(ViewDrawing from, ViewDrawing to, double tolerance, double spacing)
    {
        var targets = to.Items.Select(p => (p.Class, p.Profile, p.SolvedClass, Geometry: p.CopyGeometry())).ToArray();
        using var index = new RTree();
        for (var i = 0; i < targets.Length; i++)
        { var box = targets[i].Geometry.GetBoundingBox(true); box.Inflate(tolerance); index.Insert(box, i); }
        long samples = 0, misses = 0, wrongClass = 0, wrongProfile = 0, wrongSolvedClass = 0, fallbacks = 0, invalid = 0;
        var examples = new List<object>();
        try
        {
            foreach (var item in from.Items)
            {
                using var geometry = item.CopyGeometry();
                var length = geometry is Curve curve ? curve.GetLength() : 0;
                var count = geometry is Curve ? Math.Max(32, (int)Math.Ceiling(length / spacing)) : 0;
                var divisions = geometry is Curve divided && length > 0 ? divided.DivideByCount(count, true) : null;
                var recorded = false;
                for (var i = 0; i <= count; i++)
                {
                    var point = geometry is Curve c ? divisions?.Length == count + 1 ? c.PointAt(divisions[i]) :
                        c.PointAtNormalizedLength((double)i / count) : geometry is Point p ? p.Location : Point3d.Unset;
                    samples++;
                    if (!point.IsValid && geometry is Curve shortCurve && double.IsFinite(length) && length <= tolerance / 1.25)
                    { point = shortCurve.PointAt(shortCurve.Domain.ParameterAt((double)i / count)); fallbacks++; }
                    if (!point.IsValid)
                    {
                        invalid++; misses++;
                        if (!recorded && examples.Count < 16) { examples.Add(new { invalid = true, item.SourceIdentity, length }); recorded = true; }
                        continue;
                    }
                    bool same = false, any = false, sameClass = false, sameProfile = false, sameSolved = false;
                    index.Search(new Sphere(point, tolerance), (_, e) =>
                    {
                        if (same) return;
                        var target = targets[e.Id];
                        if (!(target.Geometry is Curve tc && tc.ClosestPoint(point, out var t) && tc.PointAt(t).DistanceTo(point) <= tolerance ||
                            target.Geometry is Point tp && tp.Location.DistanceTo(point) <= tolerance)) return;
                        any = true;
                        if (target.Class != item.Class) return;
                        sameClass = true;
                        sameProfile |= target.Profile == item.Profile; sameSolved |= target.SolvedClass == item.SolvedClass;
                        same |= target.Profile == item.Profile && target.SolvedClass == item.SolvedClass;
                    });
                    if (same) continue;
                    misses++;
                    if (examples.Count < 16 && !recorded)
                    {
                        var neighbours = new List<object>();
                        index.Search(new Sphere(point, tolerance), (_, e) =>
                        {
                            if (neighbours.Count >= 8) return;
                            var target = targets[e.Id];
                            if (target.Geometry is Curve tc && tc.ClosestPoint(point, out var t) && tc.PointAt(t).DistanceTo(point) <= tolerance ||
                                target.Geometry is Point tp && tp.Location.DistanceTo(point) <= tolerance)
                                neighbours.Add(new { target.Class, target.Profile, target.SolvedClass });
                        });
                        examples.Add(new { point = new[] { point.X, point.Y, point.Z }, item.Class, item.Profile, item.SolvedClass, item.SourceIdentity, length, neighbours });
                        recorded = true;
                    }
                    if (any && !sameClass) wrongClass++;
                    if (sameClass && !sameProfile) wrongProfile++;
                    if (sameClass && !sameSolved) wrongSolvedClass++;
                }
            }
            return new(samples, misses, wrongClass, wrongProfile, wrongSolvedClass, fallbacks, invalid, examples.ToArray());
        }
        finally { foreach (var target in targets) target.Geometry.Dispose(); }
    }
}
