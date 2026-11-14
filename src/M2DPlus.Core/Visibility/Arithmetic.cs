using System.Numerics;

namespace M2DPlus.Core.Visibility;

internal interface IScalar<T> where T : IScalar<T>
{
    static abstract T From(double value);
    static abstract T operator +(T a, T b);
    static abstract T operator -(T a, T b);
    static abstract T operator *(T a, T b);
    static abstract T operator /(T a, T b);
    static abstract int Compare(T a, T b);
    double Value { get; }
}

/// <summary>Exact rational of the supplied IEEE binary64 value, not its decimal spelling.</summary>
internal readonly struct Rational : IScalar<Rational>, IComparable<Rational>
{
    public BigInteger N { get; }
    public BigInteger D { get; }
    public Rational(BigInteger n, BigInteger d)
    {
        if (d.IsZero) throw new ArithmeticException("Zero denominator");
        if (d.Sign < 0) { n = -n; d = -d; }
        var g = BigInteger.GreatestCommonDivisor(n, d); N = n / g; D = d / g;
    }
    public static Rational From(double v)
    {
        if (!double.IsFinite(v)) throw new ArithmeticException("Nonfinite coordinate");
        long bits = BitConverter.DoubleToInt64Bits(v);
        int e = (int)((bits >> 52) & 2047);
        BigInteger n = bits & 0xfffffffffffffL;
        if (e != 0) n += BigInteger.One << 52;
        if (bits < 0) n = -n;
        int shift = e == 0 ? -1074 : e - 1075;
        return shift >= 0 ? new(n << shift, 1) : new(n, BigInteger.One << -shift);
    }
    public static Rational operator +(Rational a, Rational b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Rational operator -(Rational a, Rational b) => new(a.N * b.D - b.N * a.D, a.D * b.D);
    public static Rational operator *(Rational a, Rational b) => new(a.N * b.N, a.D * b.D);
    public static Rational operator /(Rational a, Rational b) => new(a.N * b.D, a.D * b.N);
    public static int Compare(Rational a, Rational b) => (a.N * b.D).CompareTo(b.N * a.D);
    public int CompareTo(Rational other) => Compare(this, other);
    public double Value
    {
        get
        {
            int shift = (int)Math.Max(0, Math.Max(N.GetBitLength(), D.GetBitLength()) - 1000);
            return (double)(N >> shift) / (double)(D >> shift);
        }
    }
}

internal sealed class IndeterminateException : ArithmeticException;
internal sealed class EndpointResolutionException : ArithmeticException;

/// <summary>Outward-rounded interval filter. Only certified comparisons return a sign.</summary>
internal readonly record struct Filter(double Lo, double Hi) : IScalar<Filter>
{
    public static Filter From(double v) => double.IsFinite(v) ? new(v, v) : throw new IndeterminateException();
    // Seed cached plane coefficients from their exact values. In particular an
    // exact zero stays a singleton instead of becoming an uncertain cross product.
    public static Filter FromExact(Rational exact)
    {
        double lo = exact.Value, hi = lo;
        for (int i = 0; i < 8; i++)
        {
            if (!double.IsFinite(lo) || !double.IsFinite(hi)) throw new IndeterminateException();
            int lower = Rational.Compare(Rational.From(lo), exact), upper = Rational.Compare(Rational.From(hi), exact);
            if (lower <= 0 && upper >= 0) return new(lo, hi);
            if (lower > 0) lo = Math.BitDecrement(lo);
            if (upper < 0) hi = Math.BitIncrement(hi);
        }
        throw new IndeterminateException();
    }
    public double Value => Hi - Lo <= 1e-13 ? Lo + (Hi - Lo) * .5 : throw new IndeterminateException();
    private static Filter Enclose(double lo, double hi)
    {
        if (!double.IsFinite(lo) || !double.IsFinite(hi)) throw new IndeterminateException();
        return new(Math.BitDecrement(lo), Math.BitIncrement(hi));
    }
    public static Filter operator +(Filter a, Filter b)
    {
        if (a.Lo == 0 && a.Hi == 0) return b;
        if (b.Lo == 0 && b.Hi == 0) return a;
        return Enclose(a.Lo + b.Lo, a.Hi + b.Hi);
    }
    public static Filter operator -(Filter a, Filter b)
    {
        if (a.Lo == a.Hi && b.Lo == b.Hi && a.Lo == b.Lo) return From(0);
        if (b.Lo == 0 && b.Hi == 0) return a;
        return Enclose(a.Lo - b.Hi, a.Hi - b.Lo);
    }
    public static Filter operator *(Filter a, Filter b)
    {
        if ((a.Lo == 0 && a.Hi == 0) || (b.Lo == 0 && b.Hi == 0)) return From(0);
        double p = a.Lo * b.Lo, q = a.Lo * b.Hi, r = a.Hi * b.Lo, s = a.Hi * b.Hi;
        return Enclose(Math.Min(Math.Min(p, q), Math.Min(r, s)), Math.Max(Math.Max(p, q), Math.Max(r, s)));
    }
    public static Filter operator /(Filter a, Filter b)
    {
        if (b.Lo <= 0 && b.Hi >= 0) throw new IndeterminateException();
        return a * Enclose(1 / b.Hi, 1 / b.Lo);
    }
    public static int Compare(Filter a, Filter b)
    {
        if (a.Lo > b.Hi) return 1;
        if (a.Hi < b.Lo) return -1;
        if (a.Lo == a.Hi && b.Lo == b.Hi && a.Lo == b.Lo) return 0;
        throw new IndeterminateException();
    }
}
