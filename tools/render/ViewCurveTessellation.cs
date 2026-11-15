// MIT. Local evidence collector only; no production engine changes.
using System.Text.Json;
using Rhino.Geometry;

namespace M2DPlus.Render;

public static class ViewCurveTessellation
{
    public sealed record Result(bool Success, double[][] Points, double ErrorBound, string? Error)
    {
        public bool PointOnly => Success && Points.Length > 0 && Points.All(p => p.SequenceEqual(Points[0]));
    }
    private static Result Failed(string reason) => new(false, [], 0, reason);
    private static double[] Point(double[] h) => [h[0] / h[3], h[1] / h[3], h[2] / h[3]];
    private static double Distance(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));

    // Positive rational weights keep the curve inside the Euclidean control hull.
    // Distance to a segment is convex. A bound on all controls bounds the curve;
    // continuity between the endpoints also bounds the reverse chord-to-curve distance.
    public static Result ControlPolygon(double[][] control, double tolerance)
    {
        if (!(tolerance > 0) || !double.IsFinite(tolerance) || control.Length < 2 ||
            control.Any(p => p.Length != 4 || p.Any(v => !double.IsFinite(v)) || p[3] <= 0))
            return Failed("Requires finite XYZ controls and strictly positive rational weights");
        var homogeneous = control.Select(p => new[] { p[0] * p[3], p[1] * p[3], p[2] * p[3], p[3] }).ToArray();
        if (homogeneous.Any(p => p.Any(v => !double.IsFinite(v)))) return Failed("Homogeneous coordinate overflow");
        var output = new List<double[]> { Point(homogeneous[0]) };
        var stack = new Stack<(double[][] Controls, int Depth)>(); stack.Push((homogeneous, 0));
        double maximum = 0;
        while (stack.Count > 0)
        {
            var (h, depth) = stack.Pop(); var p = h.Select(Point).ToArray();
            var a = p[0]; var b = p[^1]; var d = Enumerable.Range(0, 3).Select(i => b[i] - a[i]).ToArray();
            double lengthSquared = d.Sum(v => v * v), bound = 0;
            foreach (var q in p)
            {
                double t = lengthSquared == 0 ? 0 : Math.Clamp(Enumerable.Range(0, 3).Sum(i => (q[i] - a[i]) * d[i]) / lengthSquared, 0, 1);
                bound = Math.Max(bound, Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(q[i] - a[i] - t * d[i], 2))));
            }
            // Never collapse a small closed loop merely because it fits inside tolerance.
            if (bound <= tolerance && (lengthSquared > 0 || bound == 0))
            {
                maximum = Math.Max(maximum, bound); output.Add(b); continue;
            }
            if (depth >= 32 || output.Count + stack.Count > 1_000_000) return Failed("Subdivision budget exhausted");
            var work = h.Select(q => q.ToArray()).ToArray();
            var left = new double[h.Length][]; var right = new double[h.Length][];
            left[0] = work[0]; right[^1] = work[^1];
            for (int level = 1; level < h.Length; level++)
            {
                for (int i = 0; i < h.Length - level; i++)
                    work[i] = Enumerable.Range(0, 4).Select(k => work[i][k] * .5 + work[i + 1][k] * .5).ToArray();
                left[level] = work[0]; right[h.Length - 1 - level] = work[h.Length - 1 - level];
            }
            stack.Push((right, depth + 1)); stack.Push((left, depth + 1));
        }
        return new(true, output.ToArray(), maximum, null);
    }

    public static Result Curve(Curve curve, double tolerance)
    {
        using var nurbs = curve.ToNurbsCurve();
        if (nurbs is null || !nurbs.IsValid) return Failed("Invalid NURBS conversion");
        var points = new List<double[]>(); double bound = 0;
        for (int i = 0; i < nurbs.SpanCount; i++)
        {
            using var bezier = nurbs.ConvertSpanToBezier(i);
            if (bezier is null) return Failed("Bezier span conversion failed");
            using var span = bezier.ToNurbsCurve();
            var control = span.Points.Select(p => new[] { p.Location.X, p.Location.Y, p.Location.Z, p.Weight }).ToArray();
            var result = ControlPolygon(control, tolerance);
            if (!result.Success) return result;
            if (points.Count > 0)
            {
                double gap = Distance(points[^1], result.Points[0]);
                if (gap > tolerance) return Failed("Discontinuous Bezier spans");
                bound = Math.Max(bound, gap);
            }
            points.AddRange(result.Points); bound = Math.Max(bound, result.ErrorBound);
            if (points.Count > 1_000_000) return Failed("Curve vertex budget exhausted");
        }
        if (points.Count < 2) return Failed("Curve has no Bezier span");
        // A zero-extent curve is an exact point locus, not an unextracted line.
        // Preserve its coordinates; the common line-locus union discards zero-length spans.
        return new(true, points.ToArray(), bound, null);
    }

    public static string CurveJson(Curve curve, double tolerance) => JsonSerializer.Serialize(Curve(curve, tolerance));
    public static string ControlJson(string control, double tolerance) => JsonSerializer.Serialize(ControlPolygon(JsonSerializer.Deserialize<double[][]>(control)!, tolerance));
}
