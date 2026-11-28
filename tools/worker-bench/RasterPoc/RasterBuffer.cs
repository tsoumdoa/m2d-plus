// Stage 0 research only. Plain CPU raster, linked only into worker-bench.
internal sealed class RasterBuffer
{
    internal readonly record struct Vertex(double X, double Y, double Z);
    internal readonly record struct Triangle(Vertex A, Vertex B, Vertex C, int Face)
    {
        internal double Area => (B.X - A.X) * (C.Y - A.Y) - (B.Y - A.Y) * (C.X - A.X);
        internal double Dx => ((B.Z - A.Z) * (C.Y - A.Y) - (C.Z - A.Z) * (B.Y - A.Y)) / Area;
        internal double Dy => ((B.X - A.X) * (C.Z - A.Z) - (C.X - A.X) * (B.Z - A.Z)) / Area;
        internal double Depth(double x, double y) => A.Z + Dx * (x - A.X) + Dy * (y - A.Y);
    }
    private const int Tile = 32;
    internal int Width { get; }
    internal int Height { get; }
    internal double Pixel { get; }
    internal double MinX { get; }
    internal double MinY { get; }
    internal long Bytes => (long)Width * Height * 12;
    private readonly double[] _depth;
    private readonly int[] _ids;
    private readonly Triangle[] _triangles;

    // The requested long dimension is reduced only when the 512 MiB buffer cap requires it.
    internal RasterBuffer(double minX, double minY, double spanX, double spanY, int resolution, Triangle[] triangles, int threads)
    {
        var scale = Math.Min(resolution / Math.Max(spanX, spanY), Math.Sqrt((512d * 1024 * 1024 / 12) / (spanX * spanY)));
        Width = Math.Max(1, (int)Math.Floor(spanX * scale)); Height = Math.Max(1, (int)Math.Floor(spanY * scale));
        Pixel = Math.Max(spanX / Width, spanY / Height); MinX = minX; MinY = minY;
        _depth = new double[checked(Width * Height)]; Array.Fill(_depth, double.NegativeInfinity);
        _ids = new int[_depth.Length]; Array.Fill(_ids, -1);
        _triangles = triangles;
        var nx = (Width + Tile - 1) / Tile; var ny = (Height + Tile - 1) / Tile;
        var bins = new List<int>?[nx * ny];
        // Binning is deterministic; tiles own disjoint pixels, so raster writes need no locks.
        for (var i = 0; i < triangles.Length; i++)
        {
            var t = triangles[i]; if (Math.Abs(t.Area) < 1e-20) continue;
            var left = Math.Max(0, (int)Math.Floor((Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - minX) / Pixel));
            var right = Math.Min(Width - 1, (int)Math.Floor((Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) - minX) / Pixel));
            var bottom = Math.Max(0, (int)Math.Floor((Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - minY) / Pixel));
            var top = Math.Min(Height - 1, (int)Math.Floor((Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) - minY) / Pixel));
            if (left > right || bottom > top) continue;
            for (var y = bottom / Tile; y <= top / Tile; y++)
            for (var x = left / Tile; x <= right / Tile; x++) (bins[y * nx + x] ??= []).Add(i);
        }
        Parallel.For(0, bins.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, bin =>
        {
            if (bins[bin] is not { } members) return;
            var tileX = bin % nx * Tile; var tileY = bin / nx * Tile;
            foreach (var id in members)
            {
                var t = triangles[id]; var area = t.Area; var dx = t.Dx; var dy = t.Dy;
                var left = Math.Max(tileX, (int)Math.Floor((Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - minX) / Pixel));
                var right = Math.Min(Math.Min(Width, tileX + Tile) - 1, (int)Math.Floor((Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) - minX) / Pixel));
                var bottom = Math.Max(tileY, (int)Math.Floor((Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - minY) / Pixel));
                var top = Math.Min(Math.Min(Height, tileY + Tile) - 1, (int)Math.Floor((Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) - minY) / Pixel));
                for (var y = bottom; y <= top; y++)
                {
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
    internal bool Visible(Vertex point, ReadOnlySpan<int> adjacent, double bias, bool tangent = false)
    {
        var x = (int)Math.Floor((point.X - MinX) / Pixel); var y = (int)Math.Floor((point.Y - MinY) / Pixel);
        if (x < 0 || y < 0 || x >= Width || y >= Height) return true;
        var id = _ids[y * Width + x]; if (id < 0) return true;
        var triangle = _triangles[id];
        // A face can wrap around an object. Its front surface must not make a rear rim visible.
        // The ID shortcut is valid for an actual tangency candidate, not every topological edge.
        if (tangent) foreach (var face in adjacent) if (face == triangle.Face) return true;
        // Correct centre depth to the candidate ray, then allow uncertainty from the finite footprint.
        var slopeBias = Math.Min(Pixel * 2, Pixel * .25 * (Math.Abs(triangle.Dx) + Math.Abs(triangle.Dy)));
        return triangle.Depth(point.X, point.Y) <= point.Z + bias + slopeBias;
    }
    internal static void Controls()
    {
        Triangle[] tris = [new(new(0, 0, 2), new(10, 0, 2), new(10, 10, 2), 7), new(new(0, 0, 2), new(10, 10, 2), new(0, 10, 2), 7)];
        foreach (var threads in new[] { 1, 4 })
        {
            var grid = new RasterBuffer(0, 0, 10, 10, 128, tris, threads);
            if (grid.Visible(new(5, 5, 1), [], .001) || !grid.Visible(new(5, 5, 3), [], .001) ||
                grid.Visible(new(5, 5, 1), [7], .001) || !grid.Visible(new(5, 5, 1), [7], .001, true) || !grid.Visible(new(-1, 5, 1), [], .001))
                throw new InvalidOperationException("Raster depth/adjacency/bounds control failed");
        }
        Console.WriteLine("Raster controls passed (serial and parallel).");
    }
}
