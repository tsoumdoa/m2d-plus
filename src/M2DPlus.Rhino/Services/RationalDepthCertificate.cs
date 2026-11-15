using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Whole-span rational comparison; never infers a bound from sample points.</summary>
internal static class RationalDepthCertificate
{
    internal static bool Front(NurbsCurve candidate, NurbsCurve witness, double tolerance, double roundoff)
    {
        using var a = candidate.ToNurbsCurve();
        using var b = witness.ToNurbsCurve();
        if (a.Degree > 12 || b.Degree > 12) return false;
        a.Domain = b.Domain = new(0, 1);
        // Knot insertion preserves parameterisation. Both curves must be compared
        // at the same parameter; unrelated parameterisations simply fail the bound.
        var cuts = a.Knots.Concat(b.Knots).Where(t => t > 0 && t < 1).Distinct().Order().ToArray();
        if (cuts.Length > 256) return false;
        foreach (double t in cuts)
        {
            if (!a.Knots.Contains(t) && !a.Knots.InsertKnot(t)) return false;
            if (!b.Knots.Contains(t) && !b.Knots.InsertKnot(t)) return false;
        }
        if (!a.MakePiecewiseBezier(false) || !b.MakePiecewiseBezier(false)) return false;
        var ai = Spans(a); var bi = Spans(b);
        if (ai.Length != bi.Length || ai.Length == 0) return false;
        for (int k = 0; k < ai.Length; k++)
        {
            int i = ai[k], j = bi[k];
            if (a.Knots[i + a.Degree - 1] != b.Knots[j + b.Degree - 1] ||
                a.Knots[i + a.Degree] != b.Knots[j + b.Degree]) return false;
            using var ac = a.ConvertSpanToBezier(i); using var bc = b.ConvertSpanToBezier(j);
            if (ac is null || bc is null) return false;
            using var an = ac.ToNurbsCurve(); using var bn = bc.ToNurbsCurve();
            if (!Bound(an, bn, tolerance, roundoff)) return false;
        }
        return true;
    }

    private static int[] Spans(NurbsCurve c) => Enumerable.Range(0, c.Points.Count - c.Degree)
        .Where(i => c.Knots[i + c.Degree - 1] < c.Knots[i + c.Degree]).ToArray();

    private static bool Bound(NurbsCurve a, NurbsCurve b, double tolerance, double roundoff)
    {
        var p = a.Points.ToArray(); var q = b.Points.ToArray();
        if (p.Concat(q).Any(c => !c.Location.IsValid || !double.IsFinite(c.Weight) || c.Weight <= 0)) return false;
        var denominator = I.Of(p.Min(c => c.Weight)) * I.Of(q.Min(c => c.Weight));
        if (!double.IsFinite(denominator.Lo) || denominator.Lo <= 0) return false;
        int n = p.Length - 1, m = q.Length - 1;
        // (Q/V - P/W) = (QW - PV)/(VW). Products of Bernstein
        // bases are Bernstein bases of degree n+m with positive coefficients.
        // Their convex hull bounds every parameter, including all unsampled interiors.
        for (int k = 0; k <= n + m; k++)
        {
            var x = I.Of(0); var y = I.Of(0); var z = I.Of(0);
            for (int i = Math.Max(0, k - m); i <= Math.Min(n, k); i++)
            {
                int j = k - i;
                var factor = (I.Of(Choose(n, i)) * I.Of(Choose(m, j)) / I.Of(Choose(n + m, k))) * I.Of(p[i].Weight) * I.Of(q[j].Weight);
                x += factor * (I.Of(q[j].Location.X) - I.Of(p[i].Location.X));
                y += factor * (I.Of(q[j].Location.Y) - I.Of(p[i].Location.Y));
                z += factor * (I.Of(q[j].Location.Z) - I.Of(p[i].Location.Z));
            }
            double xy = Math.BitDecrement(tolerance / 32 * denominator.Lo);
            double depth = Math.BitIncrement(-roundoff * denominator.Lo);
            if (!x.Within(xy) || !y.Within(xy) || !double.IsFinite(z.Lo) || z.Lo < depth) return false;
        }
        return true;
    }

    private static double Choose(int n, int k)
    {
        // n <= 24: every integer coefficient is exactly representable as double.
        long value = 1;
        for (int i = 1; i <= k; i++) value = value * (n - i + 1) / i;
        return value;
    }

    private readonly record struct I(double Lo, double Hi)
    {
        internal static I Of(double x) => new(x, x);
        internal bool Within(double bound) => double.IsFinite(Lo) && double.IsFinite(Hi) && Lo >= -bound && Hi <= bound;
        public static I operator +(I a, I b) => new(Math.BitDecrement(a.Lo + b.Lo), Math.BitIncrement(a.Hi + b.Hi));
        public static I operator -(I a, I b) => new(Math.BitDecrement(a.Lo - b.Hi), Math.BitIncrement(a.Hi - b.Lo));
        public static I operator *(I a, I b)
        {
            double[] p = [a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi];
            return new(Math.BitDecrement(p.Min()), Math.BitIncrement(p.Max()));
        }
        public static I operator /(I a, I b) => a * new I(Math.BitDecrement(1 / b.Hi), Math.BitIncrement(1 / b.Lo));
    }
}
