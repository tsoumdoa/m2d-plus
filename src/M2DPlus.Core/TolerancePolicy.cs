namespace M2DPlus.Core;

/// <summary>Drawing advice only. Never changes the selected document/override tolerance.</summary>
public static class TolerancePolicy
{
    // Intermediate scales use the more demanding band until the next threshold.
    public static double RecommendedMillimetres(double scale) => scale <= 5 ? .01 : scale < 50 ? .1 : 1;

    public static string? ScaleWarning(double tolerance, double scale, DocumentUnits units)
    {
        var mm = tolerance * Settings.MetresPerUnit(units) * 1000;
        var recommended = RecommendedMillimetres(scale);
        return mm > recommended * (1 + 1e-12)
            ? FormattableString.Invariant($"Tolerance {mm:G6} mm is too coarse for scale 1:{scale:G6}; use <= {recommended:G6} mm to resolve drawing detail. Document tolerance is unchanged; tolerance = may override it. See Info for tolerance counts.")
            : null;
    }
}
