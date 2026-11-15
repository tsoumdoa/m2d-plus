namespace M2DPlus.Core.Visibility.Raster;

/// <summary>An affine map from world (x, y, z) to pixel x, pixel y and positive depth.</summary>
public readonly record struct ViewAffine(double Xx, double Xy, double Xz, double X0, double Yx, double Yy, double Yz, double Y0,
    double Dx, double Dy, double Dz, double D0)
{
    public (double X, double Y, double D) Map(double x, double y, double z) =>
        (Xx * x + Xy * y + Xz * z + X0, Yx * x + Yy * y + Yz * z + Y0, Dx * x + Dy * y + Dz * z + D0);
}

/// <summary>
/// Certified flattening of rational Bezier curves. With positive weights a rational Bezier span lies in the convex hull of
/// its (dehomogenised) control points, and an affine projection preserves that. Distance to a segment is convex, so the
/// projected span lies within max_i dist(P_i, chord) of the chord between its end points, and its depth lies within the
/// control points' depth range. Spans are split at t = 1/2 (de Casteljau, homogeneous) until that bound is small.
/// </summary>
public static class BezierFlatten
{
    public sealed class Result
    {
        /// <summary>Polyline points (pixel x, y), from the curve start to its end.</summary>
        public List<double> Points { get; } = new();
        /// <summary>Upper bound on the distance between the projected curve and the polyline, in pixels.</summary>
        public double Deviation { get; set; }
        public double MaxDepth { get; set; } = double.MinValue;
        public double MinDepth { get; set; } = double.MaxValue;
    }

    /// <summary>
    /// Appends one span (homogeneous control points wx, wy, wz, w; degree = count - 1) to <paramref name="result"/>.
    /// Returns false (and appends nothing further) if a weight is not positive or the split budget is exhausted.
    /// The first point is appended only when <paramref name="first"/>.
    /// </summary>
    public static bool Span(double[] cv, ViewAffine map, double epsilon, int maxSplits, Result result, bool first)
    {
        var n = cv.Length / 4;
        if (n < 2) return false;
        for (var i = 0; i < n; i++) if (!(cv[4 * i + 3] > 0)) return false;
        var budget = maxSplits;
        return Recurse(cv, map, epsilon, ref budget, result, first);
    }

    private static bool Recurse(double[] cv, ViewAffine map, double epsilon, ref int budget, Result result, bool first)
    {
        var n = cv.Length / 4;
        var px = new double[n]; var py = new double[n];
        double dMin = double.MaxValue, dMax = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            var w = cv[4 * i + 3];
            var (x, y, d) = map.Map(cv[4 * i] / w, cv[4 * i + 1] / w, cv[4 * i + 2] / w);
            px[i] = x; py[i] = y; dMin = Math.Min(dMin, d); dMax = Math.Max(dMax, d);
        }
        var deviation = 0.0;
        for (var i = 1; i < n - 1; i++) deviation = Math.Max(deviation, SegmentDistance(px[i], py[i], px[0], py[0], px[n - 1], py[n - 1]));
        if (deviation > epsilon && budget > 0)
        {
            budget--;
            Split(cv, out var left, out var right);
            return Recurse(left, map, epsilon, ref budget, result, first) && Recurse(right, map, epsilon, ref budget, result, false);
        }
        if (first) { result.Points.Add(px[0]); result.Points.Add(py[0]); }
        result.Points.Add(px[n - 1]); result.Points.Add(py[n - 1]);
        result.Deviation = Math.Max(result.Deviation, deviation);
        result.MaxDepth = Math.Max(result.MaxDepth, dMax); result.MinDepth = Math.Min(result.MinDepth, dMin);
        return true;
    }

    /// <summary>de Casteljau split at t = 1/2 in homogeneous coordinates (exact for rational curves).</summary>
    public static void Split(double[] cv, out double[] left, out double[] right)
    {
        var n = cv.Length / 4; var work = (double[])cv.Clone();
        left = new double[cv.Length]; right = new double[cv.Length];
        for (var c = 0; c < 4; c++) { left[c] = work[c]; right[4 * (n - 1) + c] = work[4 * (n - 1) + c]; }
        for (var level = 1; level < n; level++)
        {
            for (var i = 0; i < n - level; i++)
                for (var c = 0; c < 4; c++) work[4 * i + c] = .5 * (work[4 * i + c] + work[4 * (i + 1) + c]);
            for (var c = 0; c < 4; c++) { left[4 * level + c] = work[c]; right[4 * (n - 1 - level) + c] = work[4 * (n - 1 - level) + c]; }
        }
    }

    public static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1) : 0;
        var ex = ax + t * dx - px; var ey = ay + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
