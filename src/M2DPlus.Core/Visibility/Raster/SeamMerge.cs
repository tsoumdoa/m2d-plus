namespace M2DPlus.Core.Visibility.Raster;

/// <summary>A directed boundary segment (pixels) with the side its face lies on: +1 left, -1 right, 0 unknown (never shared).</summary>
public readonly record struct SidedSegment(double X0, double Y0, double X1, double Y1, int Side);

/// <summary>An object's occluder patch before cross-object merging.</summary>
public sealed class PatchInput
{
    public required IReadOnlyList<RasterFace> Faces { get; init; }
    public required IReadOnlyList<SidedSegment> Boundary { get; init; }
    public double Dilation { get; init; }
}

/// <summary>
/// Cross-object seam merging. Where boundary segments of two different opaque patches are collinear (within
/// <c>epsilon</c> pixels) and overlap with their faces on opposite sides, the overlap is interior to the union of the two
/// faces, exactly as for an interior edge inside one object. The patches are merged (faces concatenated; any face of either
/// object that touches a pixel bounds the depth there, both being opaque) and the overlap is removed from the boundary,
/// so the union's boundary is still contained in the remaining (dilated) segments and the coverage certificate of
/// <see cref="OccluderBuffer"/> applies unchanged. Unmatched or ambiguous segments simply stay boundary (conservative).
/// </summary>
public static class SeamMerge
{
    public static List<RasterPatch> Merge(IReadOnlyList<PatchInput> patches, double epsilon, out int sharedSegments)
    {
        sharedSegments = 0;
        var segs = new List<(int Patch, int Index, double Ax, double Ay, double Bx, double By, int Side)>();
        for (var p = 0; p < patches.Count; p++)
            for (var k = 0; k < patches[p].Boundary.Count; k++)
            {
                var s = patches[p].Boundary[k];
                segs.Add((p, k, s.X0, s.Y0, s.X1, s.Y1, s.Side));
            }
        // Bucket by canonical line (angle, offset); neighbours are searched too.
        const double qa = 1e-6; var qc = Math.Max(epsilon * 4, 1e-9);
        var buckets = new Dictionary<(long, long), List<int>>();
        var lines = new (double Dx, double Dy, double C, double Len)[segs.Count];
        for (var n = 0; n < segs.Count; n++)
        {
            var s = segs[n]; double dx = s.Bx - s.Ax, dy = s.By - s.Ay, len = Math.Sqrt(dx * dx + dy * dy);
            if (s.Side == 0 || !(len > 8 * epsilon)) { lines[n] = (0, 0, 0, 0); continue; }
            dx /= len; dy /= len;
            if (Math.Abs(dx) >= Math.Abs(dy) ? dx < 0 : dy < 0) { dx = -dx; dy = -dy; }
            var c = -dy * s.Ax + dx * s.Ay;
            lines[n] = (dx, dy, c, len);
            var key = ((long)Math.Round(Math.Atan2(dy, dx) / qa), (long)Math.Round(c / qc));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new();
            list.Add(n);
        }
        var shared = new List<(double T0, double T1)>[segs.Count];
        var parent = Enumerable.Range(0, patches.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (key, list) in buckets)
        {
            // Candidates from this bucket and its neighbours.
            var near = new List<int>();
            for (var da = -1; da <= 1; da++) for (var dc = -1; dc <= 1; dc++)
                if (buckets.TryGetValue((key.Item1 + da, key.Item2 + dc), out var other)) near.AddRange(other);
            foreach (var i in list)
            {
                var li = lines[i]; var si = segs[i];
                var sideI = si.Side * Math.Sign(((si.Bx - si.Ax) * li.Dx + (si.By - si.Ay) * li.Dy));
                double Param(double x, double y) => (x - si.Ax) * li.Dx + (y - si.Ay) * li.Dy;
                double Offset(double x, double y) => -li.Dy * (x - si.Ax) + li.Dx * (y - si.Ay);
                var ti0 = Math.Min(Param(si.Ax, si.Ay), Param(si.Bx, si.By)); var ti1 = Math.Max(Param(si.Ax, si.Ay), Param(si.Bx, si.By));
                foreach (var j in near)
                {
                    var sj = segs[j];
                    if (j == i || sj.Patch == si.Patch) continue;
                    if (Math.Abs(Offset(sj.Ax, sj.Ay)) > epsilon || Math.Abs(Offset(sj.Bx, sj.By)) > epsilon) continue;
                    var sideJ = sj.Side * Math.Sign((sj.Bx - sj.Ax) * li.Dx + (sj.By - sj.Ay) * li.Dy);
                    if (sideI == 0 || sideJ == 0 || sideI != -sideJ) continue;
                    var tj0 = Math.Min(Param(sj.Ax, sj.Ay), Param(sj.Bx, sj.By)); var tj1 = Math.Max(Param(sj.Ax, sj.Ay), Param(sj.Bx, sj.By));
                    // Shrink the shared interval by epsilon at both ends: the remaining stubs stay boundary.
                    var a = Math.Max(ti0, tj0) + epsilon; var b = Math.Min(ti1, tj1) - epsilon;
                    if (!(b > a)) continue;
                    (shared[i] ??= new()).Add((a, b));
                    var pi = Find(si.Patch); var pj = Find(sj.Patch); if (pi != pj) parent[pi] = pj;
                }
            }
        }
        // Every patch stays as it is (the buffer keeps the minimum, so coverage only grows). Each group of two or more
        // patches is added once more as a merged patch whose boundary omits the shared portions.
        var result = new List<RasterPatch>(patches.Count);
        var members = new Dictionary<int, int>();
        for (var p = 0; p < patches.Count; p++)
        {
            result.Add(new RasterPatch { Faces = patches[p].Faces, Boundary = patches[p].Boundary.SelectMany(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }).ToArray(), Dilation = patches[p].Dilation });
            members[Find(p)] = members.GetValueOrDefault(Find(p)) + 1;
        }
        var groups = new Dictionary<int, (List<RasterFace> Faces, List<double> Boundary, double Dilation)>();
        for (var p = 0; p < patches.Count; p++)
        {
            var root = Find(p);
            if (members[root] < 2) continue;
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = (new List<RasterFace>(), new List<double>(), 0);
            g.Faces.AddRange(patches[p].Faces);
            groups[root] = (g.Faces, g.Boundary, Math.Max(g.Dilation, patches[p].Dilation));
        }
        for (var n = 0; n < segs.Count; n++)
        {
            var s = segs[n];
            if (!groups.TryGetValue(Find(s.Patch), out var entry)) continue;
            var g = entry.Boundary;
            if (shared[n] is null) { g.Add(s.Ax); g.Add(s.Ay); g.Add(s.Bx); g.Add(s.By); continue; }
            sharedSegments++;
            var li = lines[n];
            double Param(double x, double y) => (x - s.Ax) * li.Dx + (y - s.Ay) * li.Dy;
            var t0 = Math.Min(Param(s.Ax, s.Ay), Param(s.Bx, s.By)); var t1 = Math.Max(Param(s.Ax, s.Ay), Param(s.Bx, s.By));
            // Points on the segment by parameter along the canonical line (exact at t0 and t1 up to rounding, covered by the dilation).
            (double, double) At(double t) { var u = (t - Param(s.Ax, s.Ay)) / (Param(s.Bx, s.By) - Param(s.Ax, s.Ay)); return (s.Ax + (s.Bx - s.Ax) * u, s.Ay + (s.By - s.Ay) * u); }
            var at = t0;
            foreach (var (a, b) in shared[n].OrderBy(x => x.T0))
            {
                if (a > at) { var (x0, y0) = At(at); var (x1, y1) = At(a); g.Add(x0); g.Add(y0); g.Add(x1); g.Add(y1); }
                at = Math.Max(at, b);
            }
            if (at < t1) { var (x0, y0) = At(at); var (x1, y1) = At(t1); g.Add(x0); g.Add(y0); g.Add(x1); g.Add(y1); }
        }
        result.AddRange(groups.Values.Select(g => new RasterPatch { Faces = g.Faces, Boundary = g.Boundary.ToArray(), Dilation = g.Dilation + epsilon }));
        return result;
    }
}
