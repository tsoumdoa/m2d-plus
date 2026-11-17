namespace M2DPlus.Core.Visibility;

/// <summary>
/// One surface as a welded triangle mesh in view coordinates (X, Y in the drawing, larger Z nearer the viewer). Every
/// triangle has a band: no point of the surface it stands for is further from it than that, and the surface maps
/// onto the mesh continuously within it. Vertices shared by index are what makes two triangles neighbours.
/// </summary>
public sealed class BandedSheet
{
    public int Id { get; init; }
    public required Vertex[] Vertices { get; init; }
    /// <summary>Three vertex indices per triangle.</summary>
    public required int[] Triangles { get; init; }
    public required double[] Bands { get; init; }
}

/// <summary>
/// Decides what triangle meshes with bands can decide about a straight candidate piece, and nothing else.
///   Hidden      a group of triangles of one sheet, connected in depth, lies wholly more than a tolerance in front of
///               the piece, has no mesh boundary near it and is crossed an odd number of times by the view ray from
///               the middle of the piece. Moving the mesh onto the surface within the bands cannot carry the surface
///               across either end of that stretch of the ray, nor its boundary across the stretch, so the surface
///               crosses it an odd number of times too.
///   Visible     no triangle within its band of the piece can hold a surface point more than a tolerance in front.
///   Unresolved  anything else, after halving the piece down to the least length.
/// The piece's own surface is treated like any other: near the piece it cannot be judged, which leaves the piece
/// Unresolved unless another group proves it Hidden. A caller that knows more about the own surface decides that
/// part itself.
/// </summary>
public sealed class BandedMeshIndex
{
    public readonly record struct Piece(double T0, double T1, VisibilityClass Class);

    public int Count { get; }
    private readonly double[] _x, _y, _z, _band, _pa, _pb, _pc, _slope, _zmin, _zmax;
    private readonly int[] _sheet; private readonly byte[] _naked; private readonly bool[] _steep;
    private readonly double _gx, _gy;
    private sealed record Level(double Cell, int Nx, int Ny, int[] Start, int[] Items);
    private readonly Level[] _levels = [];

    public BandedMeshIndex(IReadOnlyList<BandedSheet> sheets, CancellationToken token = default)
    {
        foreach (var s in sheets) if (s.Triangles.Length % 3 != 0 || s.Bands.Length != s.Triangles.Length / 3) throw new ArgumentException("A sheet needs one band per triangle.", nameof(sheets));
        Count = sheets.Sum(s => s.Triangles.Length / 3);
        _x = new double[Count * 3]; _y = new double[Count * 3]; _z = new double[Count * 3]; _sheet = new int[Count]; _naked = new byte[Count]; _band = new double[Count];
        _pa = new double[Count]; _pb = new double[Count]; _pc = new double[Count]; _slope = new double[Count]; _zmin = new double[Count]; _zmax = new double[Count]; _steep = new bool[Count];
        int t = 0; double sum = 0, gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
        foreach (var s in sheets)
        {
            token.ThrowIfCancellationRequested();
            int first = t; var edges = new Dictionary<(int, int), int>(s.Triangles.Length * 2);
            for (int i = 0; i < s.Triangles.Length; i += 3, t++)
            {
                if (!(s.Bands[i / 3] >= 0) || !double.IsFinite(s.Bands[i / 3])) throw new ArgumentException("Bands must be finite and not negative.", nameof(sheets));
                for (int k = 0; k < 3; k++)
                {
                    var p = s.Vertices[s.Triangles[i + k]]; _x[t * 3 + k] = p.X; _y[t * 3 + k] = p.Y; _z[t * 3 + k] = p.Z;
                    int a = s.Triangles[i + k], b = s.Triangles[i + (k + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
                _sheet[t] = s.Id; _band[t] = s.Bands[i / 3];
                double ax = _x[t * 3], ay = _y[t * 3], az = _z[t * 3], ux = _x[t * 3 + 1] - ax, uy = _y[t * 3 + 1] - ay, uz = _z[t * 3 + 1] - az, vx = _x[t * 3 + 2] - ax, vy = _y[t * 3 + 2] - ay, vz = _z[t * 3 + 2] - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, planar = Math.Sqrt(nx * nx + ny * ny);
                _zmin[t] = Math.Min(az, Math.Min(az + uz, az + vz)); _zmax[t] = Math.Max(az, Math.Max(az + uz, az + vz));
                if (Math.Abs(nz) <= 1e-9 * planar || nz == 0) _steep[t] = true;
                else { _pa[t] = -nx / nz; _pb[t] = -ny / nz; _pc[t] = az - _pa[t] * ax - _pb[t] * ay; _slope[t] = planar / Math.Abs(nz); }
                Box(t, out var x0, out var y0, out var x1, out var y1);
                sum += Math.Max(x1 - x0, y1 - y0); gx0 = Math.Min(gx0, x0); gy0 = Math.Min(gy0, y0); gx1 = Math.Max(gx1, x1); gy1 = Math.Max(gy1, y1);
            }
            t = first;
            for (int i = 0; i < s.Triangles.Length; i += 3, t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = s.Triangles[i + k], b = s.Triangles[i + (k + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                    if (edges[key] != 2) _naked[t] |= (byte)(1 << k);   // boundary or non-manifold: no parity argument across it
                }
        }
        if (Count == 0) return;
        // Multi-level grid: a triangle goes to the finest level where its box with its band spans a few cells.
        _gx = gx0; _gy = gy0; double baseCell = Math.Max(1e-9, sum / Count), extent = Math.Max(gx1 - gx0, gy1 - gy0);
        while (extent / baseCell > 3000) baseCell *= 1.25;
        var level = new int[Count]; int levels = 1;
        for (int i = 0; i < Count; i++)
        {
            Box(i, out var x0, out var y0, out var x1, out var y1); double size = Math.Max(x1 - x0, y1 - y0), cell = baseCell; int l = 0;
            while (size > 2 * cell && l < 15) { cell *= 4; l++; }
            level[i] = l; levels = Math.Max(levels, l + 1);
        }
        _levels = new Level[levels];
        for (int l = 0; l < levels; l++)
        {
            token.ThrowIfCancellationRequested();
            double cell = baseCell * Math.Pow(4, l); int nx = (int)((gx1 - gx0) / cell) + 1, ny = (int)((gy1 - gy0) / cell) + 1;
            var count = new int[nx * ny + 1]; int[] items = [], start = count;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (level[i] != l) continue;
                    Box(i, out var x0, out var y0, out var x1, out var y1);
                    int i0 = Clamp((x0 - _gx) / cell, nx), i1 = Clamp((x1 - _gx) / cell, nx), j0 = Clamp((y0 - _gy) / cell, ny), j1 = Clamp((y1 - _gy) / cell, ny);
                    for (int j = j0; j <= j1; j++) for (int k = i0; k <= i1; k++) { if (pass == 0) count[j * nx + k + 1]++; else items[--count[j * nx + k + 1]] = i; }
                }
                if (pass == 0)
                {
                    for (int c = 0; c < nx * ny; c++) count[c + 1] += count[c];
                    items = new int[count[nx * ny]]; start = (int[])count.Clone();
                    for (int c = nx * ny; c > 0; c--) count[c] = start[c];
                }
            }
            _levels[l] = new(cell, nx, ny, start, items);
        }
    }

    private static int Clamp(double v, int n) => Math.Clamp((int)Math.Floor(v), 0, n - 1);
    private void Box(int i, out double x0, out double y0, out double x1, out double y1)
    {
        double e = _band[i];
        x0 = Math.Min(_x[i * 3], Math.Min(_x[i * 3 + 1], _x[i * 3 + 2])) - e; x1 = Math.Max(_x[i * 3], Math.Max(_x[i * 3 + 1], _x[i * 3 + 2])) + e;
        y0 = Math.Min(_y[i * 3], Math.Min(_y[i * 3 + 1], _y[i * 3 + 2])) - e; y1 = Math.Max(_y[i * 3], Math.Max(_y[i * 3 + 1], _y[i * 3 + 2])) + e;
    }

    /// <summary>Per-thread working storage.</summary>
    public sealed class Scratch
    {
        internal int[] Stamp; internal int Id; internal readonly List<int>[] Lists = new List<int>[48];
        internal readonly List<(int Sheet, double Lo, double Hi, int Flags)> Entries = new();   // flags: 1 crossed at the middle, 2 boundary near, 4 may reach the piece
        public long Tests { get; internal set; }
        public Scratch(BandedMeshIndex index) { Stamp = new int[index.Count]; for (int i = 0; i < Lists.Length; i++) Lists[i] = new(); }
    }

    /// <summary>
    /// Classifies the straight piece from <paramref name="a"/> to <paramref name="b"/>; pieces are appended in order
    /// with parameters in [0, 1]. <paramref name="candidateError"/> is how far the true candidate may be from the
    /// straight piece, in any direction; it is added to every band. A piece no longer than
    /// <paramref name="leastLength"/> is not halved further.
    /// </summary>
    public void Classify(Vertex a, Vertex b, double tolerance, double candidateError, double leastLength, Scratch s, List<Piece> output)
    {
        if (Count == 0) { output.Add(new(0, 1, VisibilityClass.Visible)); return; }
        if (!(candidateError >= 0) || !(tolerance > 0)) throw new ArgumentOutOfRangeException(nameof(candidateError));
        var list = s.Lists[0]; list.Clear(); int id = ++s.Id;
        double qx0 = Math.Min(a.X, b.X) - candidateError - _gx, qx1 = Math.Max(a.X, b.X) + candidateError - _gx, qy0 = Math.Min(a.Y, b.Y) - candidateError - _gy, qy1 = Math.Max(a.Y, b.Y) + candidateError - _gy;
        double zFloor = Math.Min(a.Z, b.Z) - candidateError;
        foreach (var level in _levels)
        {
            if (level.Items.Length == 0) continue;
            int i0 = Clamp(qx0 / level.Cell, level.Nx), i1 = Clamp(qx1 / level.Cell, level.Nx), j0 = Clamp(qy0 / level.Cell, level.Ny), j1 = Clamp(qy1 / level.Cell, level.Ny);
            for (int j = j0; j <= j1; j++)
                for (int k = i0; k <= i1; k++)
                    for (int e = level.Start[j * level.Nx + k]; e < level.Start[j * level.Nx + k + 1]; e++)
                    {
                        int tri = level.Items[e]; if (s.Stamp[tri] == id) continue; s.Stamp[tri] = id;
                        if (_zmax[tri] + _band[tri] - zFloor <= tolerance) continue;   // wholly behind the piece: can never be in front
                        list.Add(tri);
                    }
        }
        int first = output.Count;
        Classify(a, b, 0, 1, tolerance, candidateError, leastLength, s, 0, output);
        int w = first;
        for (int i = first; i < output.Count; i++)
        {
            if (w > first && output[w - 1].Class == output[i].Class) output[w - 1] = output[w - 1] with { T1 = output[i].T1 };
            else output[w++] = output[i];
        }
        output.RemoveRange(w, output.Count - w);
    }

    private static Vertex Lerp(Vertex a, Vertex b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    private void Classify(Vertex a0, Vertex b0, double t0, double t1, double tol, double error, double least, Scratch s, int depth, List<Piece> output)
    {
        Vertex a = Lerp(a0, b0, t0), b = Lerp(a0, b0, t1), m = Lerp(a0, b0, (t0 + t1) / 2);
        var list = s.Lists[depth]; var sub = s.Lists[depth + 1]; sub.Clear(); var entries = s.Entries; entries.Clear();
        double zcMin = Math.Min(a.Z, b.Z), zcMax = Math.Max(a.Z, b.Z);
        foreach (int t in list)
        {
            double e = _band[t] + error;
            if (!Near(t, a, b, e)) continue;
            sub.Add(t); s.Tests++;
            // Depth of the triangle over the piece, measured from the piece: [lo, hi].
            double lo = _zmin[t] - zcMax, hi = _zmax[t] - zcMin;
            if (!_steep[t])
            {
                double ga = _pa[t] * a.X + _pb[t] * a.Y + _pc[t] - a.Z, gb = _pa[t] * b.X + _pb[t] * b.Y + _pc[t] - b.Z, w = e * _slope[t];
                double lo2 = Math.Min(ga, gb) - w, hi2 = Math.Max(ga, gb) + w;
                if (lo2 > hi || hi2 < lo) continue;
                lo = Math.Max(lo, lo2); hi = Math.Min(hi, hi2);
            }
            lo -= e; hi += e;
            if (hi <= tol) continue;                                    // cannot hold a surface point in front
            int flags = lo > tol ? (PointIn(t, m) ? 1 : 0) | (_naked[t] != 0 && NakedNear(t, a, b, e) ? 2 : 0) : 4;
            entries.Add((_sheet[t], lo, hi, flags));
        }
        if (entries.Count == 0) { output.Add(new(t0, t1, VisibilityClass.Visible)); return; }
        entries.Sort(static (p, q) => p.Sheet != q.Sheet ? p.Sheet.CompareTo(q.Sheet) : p.Lo.CompareTo(q.Lo));
        bool hidden = false;
        for (int i = 0; i < entries.Count && !hidden;)
        {
            int sheet = entries[i].Sheet; double end = entries[i].Hi; int crossings = 0, flags = 0, j = i;
            for (; j < entries.Count && entries[j].Sheet == sheet && (j == i || entries[j].Lo <= end); j++)
            {
                end = Math.Max(end, entries[j].Hi); flags |= entries[j].Flags; crossings += entries[j].Flags & 1;
            }
            if ((flags & 6) == 0 && (crossings & 1) == 1) hidden = true;
            i = j;
        }
        if (hidden) { output.Add(new(t0, t1, VisibilityClass.Hidden)); return; }
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        if (dx * dx + dy * dy + dz * dz <= least * least || depth + 2 >= s.Lists.Length) { output.Add(new(t0, t1, VisibilityClass.Unresolved)); return; }
        double tm = (t0 + t1) / 2;
        Classify(a0, b0, t0, tm, tol, error, least, s, depth + 1, output);
        // The first half reused the deeper lists; rebuild this level's list for the second half.
        sub = s.Lists[depth + 1]; sub.Clear();
        foreach (int t in list) if (Near(t, a, b, _band[t] + error)) sub.Add(t);
        Classify(a0, b0, tm, t1, tol, error, least, s, depth + 1, output);
    }

    private static double Orient(double ax, double ay, double bx, double by, double px, double py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    /// <summary>Point in projected triangle with a symbolic perturbation, evaluated on canonically ordered edges, so
    /// that of two triangles sharing an edge exactly one holds a point on it.</summary>
    private bool PointIn(int t, Vertex p)
    {
        int o = t * 3;
        for (int k = 0; k < 3; k++)
        {
            double ax = _x[o + k], ay = _y[o + k], bx = _x[o + (k + 1) % 3], by = _y[o + (k + 1) % 3], cx = _x[o + (k + 2) % 3], cy = _y[o + (k + 2) % 3];
            if (ax > bx || ax == bx && ay > by) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
            double side = Orient(ax, ay, bx, by, cx, cy); if (side == 0) return false;
            double at = Orient(ax, ay, bx, by, p.X, p.Y);
            if (at == 0) at = by != ay ? -(by - ay) : bx - ax;
            if (at > 0 != side > 0) return false;
        }
        return true;
    }

    private static double PointSegment2(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, den = dx * dx + dy * dy, t = den == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / den, 0, 1);
        double ex = px - ax - dx * t, ey = py - ay - dy * t; return ex * ex + ey * ey;
    }
    private static bool SegmentsWithin(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, double e2)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy), o2 = Orient(ax, ay, bx, by, dx, dy), o3 = Orient(cx, cy, dx, dy, ax, ay), o4 = Orient(cx, cy, dx, dy, bx, by);
        if ((o1 > 0) != (o2 > 0) && (o3 > 0) != (o4 > 0)) return true;
        return PointSegment2(ax, ay, cx, cy, dx, dy) <= e2 || PointSegment2(bx, by, cx, cy, dx, dy) <= e2 || PointSegment2(cx, cy, ax, ay, bx, by) <= e2 || PointSegment2(dx, dy, ax, ay, bx, by) <= e2;
    }
    private bool Inside(int t, double px, double py)
    {
        int o = t * 3;
        double s0 = Orient(_x[o], _y[o], _x[o + 1], _y[o + 1], px, py), s1 = Orient(_x[o + 1], _y[o + 1], _x[o + 2], _y[o + 2], px, py), s2 = Orient(_x[o + 2], _y[o + 2], _x[o], _y[o], px, py);
        return s0 >= 0 && s1 >= 0 && s2 >= 0 || s0 <= 0 && s1 <= 0 && s2 <= 0;
    }
    /// <summary>Is the projected triangle within e of the segment a-b in the drawing?</summary>
    private bool Near(int t, Vertex a, Vertex b, double e)
    {
        int o = t * 3;
        double x0 = Math.Min(a.X, b.X) - e, x1 = Math.Max(a.X, b.X) + e, y0 = Math.Min(a.Y, b.Y) - e, y1 = Math.Max(a.Y, b.Y) + e;
        if (Math.Max(_x[o], Math.Max(_x[o + 1], _x[o + 2])) < x0 || Math.Min(_x[o], Math.Min(_x[o + 1], _x[o + 2])) > x1 ||
            Math.Max(_y[o], Math.Max(_y[o + 1], _y[o + 2])) < y0 || Math.Min(_y[o], Math.Min(_y[o + 1], _y[o + 2])) > y1) return false;
        if (Inside(t, a.X, a.Y) || Inside(t, b.X, b.Y)) return true;
        double e2 = e * e;
        for (int k = 0; k < 3; k++)
            if (SegmentsWithin(a.X, a.Y, b.X, b.Y, _x[o + k], _y[o + k], _x[o + (k + 1) % 3], _y[o + (k + 1) % 3], e2)) return true;
        return false;
    }
    private bool NakedNear(int t, Vertex a, Vertex b, double e)
    {
        int o = t * 3; double e2 = e * e;
        for (int k = 0; k < 3; k++)
            if ((_naked[t] & (1 << k)) != 0 && SegmentsWithin(a.X, a.Y, b.X, b.Y, _x[o + k], _y[o + k], _x[o + (k + 1) % 3], _y[o + (k + 1) % 3], e2)) return true;
        return false;
    }
}
