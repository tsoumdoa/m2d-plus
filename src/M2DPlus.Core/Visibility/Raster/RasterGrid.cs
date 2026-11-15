namespace M2DPlus.Core.Visibility.Raster;

/// <summary>
/// An orthographic screen grid. Pixel (i, j) is the closed square [i, i+1] x [j, j+1] in pixel coordinates;
/// view-plane coordinates map as x = (u - U0) / Pixel, y = (v - V0) / Pixel. Depth is a separate scalar that
/// increases away from the viewer and is always positive (callers offset it).
/// </summary>
public sealed class RasterGrid
{
    public double U0 { get; }
    public double V0 { get; }
    public double Pixel { get; }
    public int Width { get; }
    public int Height { get; }
    public RasterGrid(double u0, double v0, double pixel, int width, int height)
    {
        if (!(pixel > 0) || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(pixel));
        U0 = u0; V0 = v0; Pixel = pixel; Width = width; Height = height;
    }
    /// <summary>A grid covering [uMin, uMax] x [vMin, vMax] with at least <paramref name="border"/> whole pixels
    /// outside it on every side, whose longer side has about <paramref name="maxSide"/> pixels.</summary>
    public static RasterGrid Fit(double uMin, double uMax, double vMin, double vMax, int maxSide, int border = 2)
    {
        var w = Math.Max(uMax - uMin, 1e-9); var h = Math.Max(vMax - vMin, 1e-9);
        var pixel = Math.Max(w, h) / Math.Max(16, maxSide - 2 * border);
        var width = (int)Math.Ceiling(w / pixel) + 2 * border; var height = (int)Math.Ceiling(h / pixel) + 2 * border;
        return new(uMin - border * pixel, vMin - border * pixel, pixel, width, height);
    }
    public double X(double u) => (u - U0) / Pixel;
    public double Y(double v) => (v - V0) / Pixel;
    public long Pixels => (long)Width * Height;
}

/// <summary>Conservative scan primitives over a pixel window. All coordinates are in pixels.</summary>
public static class ConservativeScan
{
    /// <summary>
    /// Visits every pixel of the window whose closed square comes within <paramref name="r"/> (Chebyshev) of the
    /// closed segment. This is a superset of the pixels the segment touches, so a pixel that is NOT visited has a
    /// square at distance greater than r from the segment. r must include any coordinate error of the endpoints.
    /// </summary>
    public static void Touch(double x0, double y0, double x1, double y1, double r, int iMin, int jMin, int iMax, int jMax, Action<int, int> visit)
    {
        var jLo = Math.Max(jMin, (int)Math.Floor(Math.Min(y0, y1) - r));
        var jHi = Math.Min(jMax, (int)Math.Floor(Math.Max(y0, y1) + r));
        var dy = y1 - y0; var dx = x1 - x0;
        for (var j = jLo; j <= jHi; j++)
        {
            double xa, xb;
            if (Math.Abs(dy) < 1e-300) { xa = Math.Min(x0, x1); xb = Math.Max(x0, x1); }
            else
            {
                var t0 = (j - r - y0) / dy; var t1 = (j + 1 + r - y0) / dy;
                if (t0 > t1) (t0, t1) = (t1, t0);
                t0 = Math.Max(t0, 0); t1 = Math.Min(t1, 1);
                if (t0 > t1) continue;
                xa = x0 + t0 * dx; xb = x0 + t1 * dx;
                if (xa > xb) (xa, xb) = (xb, xa);
            }
            var iLo = Math.Max(iMin, (int)Math.Floor(xa - r)); var iHi = Math.Min(iMax, (int)Math.Floor(xb + r));
            for (var i = iLo; i <= iHi; i++) visit(i, j);
        }
    }

    /// <summary>
    /// Even-odd fill: visits each window pixel whose centre lies inside the region bounded by the closed loops.
    /// Loops are flattened (x, y) pairs; <paramref name="starts"/> holds each loop's first point index.
    /// This is not conservative on its own; callers combine it with a boundary-touch mask.
    /// </summary>
    public static void Centres(double[] xy, int[] starts, int iMin, int jMin, int iMax, int jMax, Action<int, int> visit)
    {
        var points = xy.Length / 2; if (points < 3) return;
        double yMin = double.MaxValue, yMax = double.MinValue;
        for (var k = 0; k < points; k++) { yMin = Math.Min(yMin, xy[2 * k + 1]); yMax = Math.Max(yMax, xy[2 * k + 1]); }
        var jLo = Math.Max(jMin, (int)Math.Floor(yMin - .5)); var jHi = Math.Min(jMax, (int)Math.Ceiling(yMax - .5));
        var crossings = new List<double>(8);
        for (var j = jLo; j <= jHi; j++)
        {
            var yc = j + .5; crossings.Clear();
            for (var loop = 0; loop < starts.Length; loop++)
            {
                var first = starts[loop]; var end = loop + 1 < starts.Length ? starts[loop + 1] : points;
                for (var k = first; k < end; k++)
                {
                    var n = k + 1 < end ? k + 1 : first;
                    double ax = xy[2 * k], ay = xy[2 * k + 1], bx = xy[2 * n], by = xy[2 * n + 1];
                    if ((ay <= yc) == (by <= yc)) continue;
                    crossings.Add(ax + (yc - ay) * (bx - ax) / (by - ay));
                }
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var iLo = Math.Max(iMin, (int)Math.Ceiling(crossings[k] - .5)); var iHi = Math.Min(iMax, (int)Math.Floor(crossings[k + 1] - .5));
                for (var i = iLo; i <= iHi; i++) visit(i, j);
            }
        }
    }

    /// <summary>
    /// Nonzero-winding fill over directed segments (x0, y0, x1, y1): visits each window pixel whose centre has nonzero winding
    /// number with respect to the segments, which must form closed cycles. Not conservative on its own.
    /// </summary>
    public static void NonZeroCentres(double[] segments, int iMin, int jMin, int iMax, int jMax, Action<int, int> visit)
    {
        if (segments.Length < 12) return;
        double yMin = double.MaxValue, yMax = double.MinValue;
        for (var k = 0; k + 3 < segments.Length; k += 4) { yMin = Math.Min(yMin, Math.Min(segments[k + 1], segments[k + 3])); yMax = Math.Max(yMax, Math.Max(segments[k + 1], segments[k + 3])); }
        var jLo = Math.Max(jMin, (int)Math.Floor(yMin - .5)); var jHi = Math.Min(jMax, (int)Math.Ceiling(yMax - .5));
        var crossings = new List<(double X, int Dir)>(8);
        for (var j = jLo; j <= jHi; j++)
        {
            var yc = j + .5; crossings.Clear();
            for (var k = 0; k + 3 < segments.Length; k += 4)
            {
                double ax = segments[k], ay = segments[k + 1], bx = segments[k + 2], by = segments[k + 3];
                if ((ay <= yc) == (by <= yc)) continue;
                crossings.Add((ax + (yc - ay) * (bx - ax) / (by - ay), by > ay ? 1 : -1));
            }
            if (crossings.Count < 2) continue;
            crossings.Sort((p, q) => p.X.CompareTo(q.X));
            var winding = 0;
            for (var k = 0; k + 1 < crossings.Count; k++)
            {
                winding += crossings[k].Dir;
                if (winding == 0) continue;
                var iLo = Math.Max(iMin, (int)Math.Ceiling(crossings[k].X - .5)); var iHi = Math.Min(iMax, (int)Math.Floor(crossings[k + 1].X - .5));
                for (var i = iLo; i <= iHi; i++) visit(i, j);
            }
        }
    }

    /// <summary>The smallest float not below <paramref name="value"/>.</summary>
    public static float Up(double value) { var f = (float)value; return f < value ? MathF.BitIncrement(f) : f; }
    /// <summary>The largest float not above <paramref name="value"/>.</summary>
    public static float Down(double value) { var f = (float)value; return f > value ? MathF.BitDecrement(f) : f; }
}
