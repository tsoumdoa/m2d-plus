using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal static class CurveIntervals
{
    // LineCurve.Trim can return a collapsed proxy on tiny nonzero intervals.
    // Construct linear subintervals from their actual endpoints and retain domains.
    internal static Curve? Trim(Curve curve, Interval interval)
    {
        if (curve is LineCurve)
        {
            var a = curve.PointAt(interval.Min); var b = curve.PointAt(interval.Max);
            return a == b ? null : new LineCurve(a, b) { Domain = interval };
        }
        return interval == curve.Domain ? curve.DuplicateCurve() : curve.Trim(interval);
    }
}
