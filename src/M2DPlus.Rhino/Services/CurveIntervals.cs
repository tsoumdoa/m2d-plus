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
        if (interval == curve.Domain) return curve.DuplicateCurve();
        var trimmed = curve.Trim(interval);
        if (trimmed is not null || curve is not PolyCurve polycurve || !interval.IsIncreasing ||
            !curve.Domain.IncludesInterval(interval)) return trimmed;

        // ON_PolyCurve::Trim can refuse a positive interval close to a segment
        // boundary even though the constituent curve can extract it exactly.
        // Work in each segment's own domain; never replace a curved interval by
        // a chord, snap its ends, or discard it because it is below tolerance.
        var result = new PolyCurve();
        try
        {
            for (int i = 0; i < polycurve.SegmentCount; i++)
            {
                var domain = polycurve.SegmentDomain(i);
                double a = Math.Max(interval.Min, domain.Min), b = Math.Min(interval.Max, domain.Max);
                if (b <= a) continue;
                using var segment = polycurve.SegmentCurve(i);
                var local = new Interval(segment.Domain.ParameterAt(domain.NormalizedParameterAt(a)),
                    segment.Domain.ParameterAt(domain.NormalizedParameterAt(b)));
                using var part = Trim(segment, local);
                if (part is null) { result.Dispose(); return null; }
                part.Domain = new(a, b);
                if (!result.AppendSegment(part)) { result.Dispose(); return null; }
            }
            if (result.SegmentCount == 0) { result.Dispose(); return null; }
            result.Domain = interval;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
