namespace M2DPlus.Core.Experimental.RasterVisibility;

/// <summary>CPU depth broad phase plus exact projected triangle/ray tests. Visibility remains tessellation-limited.</summary>
public sealed class RasterBuffer
{
    public readonly record struct Vertex(double X, double Y, double Z);
    public readonly record struct Triangle(Vertex A, Vertex B, Vertex C, int Face)
    {
        public double Area => (B.X - A.X) * (C.Y - A.Y) - (B.Y - A.Y) * (C.X - A.X);
        public bool Degenerate
        {
            get
            {
                static double EdgeSquared(Vertex a, Vertex b) => (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
                var scale = Math.Max(EdgeSquared(A, B), Math.Max(EdgeSquared(A, C), EdgeSquared(B, C)));
                return Math.Abs(Area) <= 1e-12 * scale;
            }
        }
        public double Dx => ((B.Z - A.Z) * (C.Y - A.Y) - (C.Z - A.Z) * (B.Y - A.Y)) / Area;
        public double Dy => ((B.X - A.X) * (C.Z - A.Z) - (C.X - A.X) * (B.Z - A.Z)) / Area;
        public double Depth(double x, double y) => A.Z + Dx * (x - A.X) + Dy * (y - A.Y);
    }
    private const int Tile = 32;
    public long EstimatedBytes { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public double Pixel { get; }
    public double MinX { get; }
    public double MinY { get; }
    public long Bytes => (long)Width * Height * 12;
    private readonly double[] _depth;
    private readonly int[] _ids;
    private readonly Triangle[] _triangles;
    private readonly List<int>?[] _bins;
    private readonly int _tilesX;

    // Clamp the requested long dimension to the image ceiling and remaining projection memory.
    public RasterBuffer(double minX, double minY, double spanX, double spanY, int resolution, Triangle[] triangles, int threads, long budgetBytes = long.MaxValue, CancellationToken token = default)
    {
        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(spanX) || !double.IsFinite(spanY) || spanX <= 0 || spanY <= 0 || resolution < 1 || threads < 1)
            throw new ArgumentOutOfRangeException(nameof(spanX));
        var available = Math.Min(512L * 1024 * 1024, budgetBytes - RasterPolicy.MeshBytes(triangles.Length) - 16 * RasterPolicy.MiB);
        if (available < 1024 * 1024) throw new NotSupportedException("raster memory admission: insufficient room for triangles and image");
        var scale = Math.Min(resolution / Math.Max(spanX, spanY), Math.Sqrt((available / 12d) / (spanX * spanY)));
        Width = Math.Max(1, (int)Math.Floor(spanX * scale)); Height = Math.Max(1, (int)Math.Floor(spanY * scale));
        Pixel = Math.Max(spanX / Width, spanY / Height); MinX = minX; MinY = minY;
        _depth = new double[checked(Width * Height)]; Array.Fill(_depth, double.NegativeInfinity);
        _ids = new int[_depth.Length]; Array.Fill(_ids, -1);
        _triangles = triangles;
        var nx = (Width + Tile - 1) / Tile; var ny = (Height + Tile - 1) / Tile;
        var bins = _bins = new List<int>?[nx * ny]; _tilesX = nx;
        EstimatedBytes = checked(RasterPolicy.MeshBytes(triangles.Length) + Bytes + (long)bins.Length * 40);
        // Binning is deterministic; tiles own disjoint pixels, so raster writes need no locks.
        for (var i = 0; i < triangles.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var t = triangles[i]; if (t.Degenerate) continue;
            var left = Math.Max(0, (int)Math.Floor((Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - minX) / Pixel));
            var right = Math.Min(Width - 1, (int)Math.Floor((Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) - minX) / Pixel));
            var bottom = Math.Max(0, (int)Math.Floor((Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - minY) / Pixel));
            var top = Math.Min(Height - 1, (int)Math.Floor((Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) - minY) / Pixel));
            if (left > right || bottom > top) continue;
            for (var y = bottom / Tile; y <= top / Tile; y++)
            for (var x = left / Tile; x <= right / Tile; x++)
            {
                EstimatedBytes += 8; // List capacity growth, not just its four-byte payload.
                if (EstimatedBytes > budgetBytes) throw new NotSupportedException("raster memory admission: triangle bins exceed budget");
                (bins[y * nx + x] ??= []).Add(i);
            }
        }
        Parallel.For(0, bins.Length, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, bin =>
        {
            if (bins[bin] is not { } members) return;
            var tileX = bin % nx * Tile; var tileY = bin / nx * Tile;
            foreach (var id in members)
            {
                token.ThrowIfCancellationRequested();
                var t = triangles[id]; var area = t.Area; var dx = t.Dx; var dy = t.Dy;
                var left = Math.Max(tileX, (int)Math.Floor((Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - minX) / Pixel));
                var right = Math.Min(Math.Min(Width, tileX + Tile) - 1, (int)Math.Floor((Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) - minX) / Pixel));
                var bottom = Math.Max(tileY, (int)Math.Floor((Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - minY) / Pixel));
                var top = Math.Min(Math.Min(Height, tileY + Tile) - 1, (int)Math.Floor((Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) - minY) / Pixel));
                for (var y = bottom; y <= top; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var py = minY + (y + .5) * Pixel;
                    for (var x = left; x <= right; x++)
                    {
                        var px = minX + (x + .5) * Pixel;
                        var u = ((t.B.X - px) * (t.C.Y - py) - (t.B.Y - py) * (t.C.X - px)) / area;
                        var v = ((t.C.X - px) * (t.A.Y - py) - (t.C.Y - py) * (t.A.X - px)) / area;
                        if (u < -1e-10 || v < -1e-10 || u + v > 1 + 1e-10) continue;
                        var z = t.A.Z + dx * (px - t.A.X) + dy * (py - t.A.Y); var slot = y * Width + x;
                        if (z > _depth[slot]) { _depth[slot] = z; _ids[slot] = id; }
                    }
                }
            }
        });
    }
    public bool Visible(Vertex point, double bias, CancellationToken token = default)
    {
        var x = (int)Math.Floor((point.X - MinX) / Pixel); var y = (int)Math.Floor((point.Y - MinY) / Pixel);
        if (x < 0 || y < 0 || x >= Width || y >= Height) return true;
        // The buffer is a broad phase only. Test the candidate's own ray against
        // actual triangles in its tile, including subpixel triangles with no centre hit.
        // No face ID can prove visibility on a folded or wrapping face.
        var front = _ids[y * Width + x];
        bool Occludes(int id)
        {
            var t = _triangles[id];
            if (Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z)) <= point.Z + bias) return false;
            var area = t.Area;
            var u = ((t.B.X - point.X) * (t.C.Y - point.Y) - (t.B.Y - point.Y) * (t.C.X - point.X)) / area;
            var v = ((t.C.X - point.X) * (t.A.Y - point.Y) - (t.C.Y - point.Y) * (t.A.X - point.X)) / area;
            return u >= -1e-10 && v >= -1e-10 && u + v <= 1 + 1e-10 && t.Depth(point.X, point.Y) > point.Z + bias;
        }
        if (front >= 0 && Occludes(front)) return false;
        if (_bins[y / Tile * _tilesX + x / Tile] is { } members)
            for (var i = 0; i < members.Count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var id = members[i]; if (id != front && Occludes(id)) return false;
            }
        return true;
    }

}
