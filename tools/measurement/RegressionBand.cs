namespace M2DPlus.Measurements;

// Percent is in percentage points, not a 0..1 fraction. Gate both absolute error
// and its share of visible length so adding geometry cannot dilute a regression.
internal sealed record RegressionBand(double WrongVisibleMm, double WrongVisiblePercent,
    double WrongHiddenMm, double WrongHiddenPercent,
    double ToleranceMm, double TolerancePercent, double HiddenToleranceMm, double HiddenTolerancePercent)
{
    internal bool Accepts(double mm, double percent, double hiddenMm, double hiddenPercent) =>
        Within(mm, WrongVisibleMm, ToleranceMm) && Within(percent, WrongVisiblePercent, TolerancePercent) &&
        Within(hiddenMm, WrongHiddenMm, HiddenToleranceMm) && Within(hiddenPercent, WrongHiddenPercent, HiddenTolerancePercent);
    private static bool Within(double value, double baseline, double tolerance) =>
        double.IsFinite(value) && value >= 0 && value <= baseline + tolerance;

    internal static RegressionBand Observe(IEnumerable<DrawingComparison.Result> observations, double marginMm)
    {
        var samples = observations.ToArray();
        if (samples.Length < 2 || !double.IsFinite(marginMm) || marginMm < 0)
            throw new InvalidDataException("At least two observations and a finite margin are required");
        var visible = samples.Select(s => (Mm: s.WrongVisibleMm, Percent: s.WrongVisiblePercent,
            Length: s.PipesToHld[M2DPlus.Core.LineClass.Visible].Length)).ToArray();
        var hidden = samples.Select(s => (Mm: s.WrongHiddenMm, Percent: s.WrongHiddenPercent,
            Length: s.HldToPipes[M2DPlus.Core.LineClass.Visible].Length)).ToArray();
        if (visible.Concat(hidden).Any(s => !double.IsFinite(s.Mm) || !double.IsFinite(s.Percent) ||
            !double.IsFinite(s.Length) || s.Mm < 0 || s.Percent < 0 || s.Length < 0 || s.Mm > s.Length))
            throw new InvalidDataException("Invalid calibration observation");
        var v = visible.Max(s => s.Mm); var vp = visible.Max(s => s.Percent);
        var h = hidden.Max(s => s.Mm); var hp = hidden.Max(s => s.Percent);
        return new(v, vp, h, hp, v - visible.Min(s => s.Mm) + marginMm,
            vp - visible.Min(s => s.Percent) + visible.Max(s => s.Length == 0 ? 0 : 100 * marginMm / s.Length),
            h - hidden.Min(s => s.Mm) + marginMm,
            hp - hidden.Min(s => s.Percent) + hidden.Max(s => s.Length == 0 ? 0 : 100 * marginMm / s.Length));
    }
}
