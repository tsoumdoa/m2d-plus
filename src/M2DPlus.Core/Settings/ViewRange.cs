using System.Collections.Immutable;

namespace M2DPlus.Core;

public enum DepthRegime { Volume, SectionOnly, UncertainThinVolume }
public enum RangeClassification { Visible, Beyond, Clipped, SectionOnly, Uncertain }

/// <summary>Certified affine depth over a parameter interval, positive behind the view plane (d = -z).
/// For general curves the host must find threshold roots geometrically, then supply monotone/affine pieces.</summary>
public readonly record struct DepthSpan(double ParameterStart, double ParameterEnd, double DepthStart, double DepthEnd);
public readonly record struct DepthInterval(double Minimum, double Maximum)
{
    public double Representative => Minimum / 2 + Maximum / 2;
}
public sealed record RangePiece(double ParameterStart, double ParameterEnd, DepthInterval Depths,
    RangeClassification Classification, double Intensity);
public sealed record RangePartition(ImmutableArray<double> SplitParameters, ImmutableArray<RangePiece> Pieces);

/// <summary>Pure policy only. Scope-aware physical clipping, section topology and visibility remain host/engine work.</summary>
public sealed class ViewRange
{
    public double Depth { get; }
    public double? Beyond { get; }
    public FarClipMode FarClip { get; }
    public bool Fade { get; }
    public double FadeStart { get; }
    public double FadeEnd { get; }
    public double FadeLimit { get; }
    public DepthRegime Regime { get; }
    public bool ClipsFar => FarClip != FarClipMode.None;
    public bool GenerateFarClipLines => FarClip == FarClipMode.Line && Regime == DepthRegime.Volume;
    public bool BeyondIgnored { get; }

    public ViewRange(EffectiveSettings settings) : this(settings.Depth, settings.Beyond, settings.FarClip,
        settings.Fade, settings.FadeStart, settings.FadeEnd, settings.FadeLimit, settings.Tolerance) { }

    public ViewRange(double depth, double? beyond = null, FarClipMode farClip = FarClipMode.NoLine,
        bool fade = false, double fadeStart = 0, double fadeEnd = 100, double fadeLimit = 30, double tolerance = .001)
    {
        if (!double.IsFinite(depth) || depth < 0 || beyond is double b && (!double.IsFinite(b) || b < 0) ||
            !double.IsFinite(tolerance) || tolerance <= 0 || !Enum.IsDefined(farClip))
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth/beyond must be nonnegative and tolerance positive.");
        if (new[] { fadeStart, fadeEnd, fadeLimit }.Any(p => !double.IsFinite(p) || p < 0 || p > 100) || fadeStart > fadeEnd)
            throw new ArgumentOutOfRangeException(nameof(fadeStart), "Fade requires 0 <= start <= end <= 100 and 0 <= limit <= 100.");
        Depth = depth; FarClip = farClip; Fade = fade; FadeStart = fadeStart; FadeEnd = fadeEnd; FadeLimit = fadeLimit;
        BeyondIgnored = farClip != FarClipMode.None && beyond >= depth;
        Beyond = BeyondIgnored ? null : beyond;
        Regime = farClip == FarClipMode.None ? DepthRegime.Volume : depth == 0 ? DepthRegime.SectionOnly
            : depth <= tolerance ? DepthRegime.UncertainThinVolume : DepthRegime.Volume;
    }

    public double Intensity(DepthInterval interval, LineClass lineClass = LineClass.Visible)
    {
        Validate(interval);
        return Intensity(interval.Representative, lineClass);
    }

    /// <summary>Returns intensity in [0,1]. Equal start/end is an immediate step at that depth.
    /// A zero fade reference keeps full intensity; section-only never divides by zero.</summary>
    public double Intensity(double representativeDepth, LineClass lineClass = LineClass.Visible)
    {
        if (!double.IsFinite(representativeDepth)) throw new ArgumentOutOfRangeException(nameof(representativeDepth));
        if (!Fade || Depth == 0 || lineClass is LineClass.Cut or LineClass.Hatch or LineClass.Unresolved) return 1;
        var percent = representativeDepth / Depth * 100;
        var limit = FadeLimit / 100;
        if (percent < FadeStart) return 1;
        if (percent >= FadeEnd) return limit;
        return 1 - (1 - limit) * ((percent - FadeStart) / (FadeEnd - FadeStart));
    }

    public static double QuantizeForBake(double intensity)
    {
        if (!double.IsFinite(intensity) || intensity < 0 || intensity > 1) throw new ArgumentOutOfRangeException(nameof(intensity));
        return Math.Round(intensity * 20, MidpointRounding.AwayFromZero) / 20;
    }

    public RangeClassification Classify(double depth, bool participatesInClipping = true)
    {
        if (!double.IsFinite(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        if (participatesInClipping)
        {
            if (Regime == DepthRegime.SectionOnly) return RangeClassification.SectionOnly;
            if (depth < 0 || ClipsFar && depth > Depth) return RangeClassification.Clipped;
            if (Regime == DepthRegime.UncertainThinVolume) return RangeClassification.Uncertain;
        }
        return Beyond is double b && depth > b ? RangeClassification.Beyond : RangeClassification.Visible;
    }

    /// <summary>Splits exactly at Beyond/near/far thresholds, never at fade thresholds. No duplicated intervals.</summary>
    public RangePartition Partition(DepthSpan span, bool participatesInClipping = true)
    {
        if (!double.IsFinite(span.ParameterStart) || !double.IsFinite(span.ParameterEnd) || span.ParameterStart >= span.ParameterEnd ||
            !double.IsFinite(span.DepthStart) || !double.IsFinite(span.DepthEnd) ||
            !double.IsFinite(span.ParameterEnd - span.ParameterStart) || !double.IsFinite(span.DepthEnd - span.DepthStart))
            throw new ArgumentException("Expected a finite affine depth span with increasing parameters.", nameof(span));
        var splits = new SortedSet<double>();
        if (Beyond is double b) Add(b);
        if (participatesInClipping && Regime != DepthRegime.SectionOnly)
        {
            Add(0);
            if (ClipsFar) Add(Depth);
        }
        var parameters = new[] { span.ParameterStart }.Concat(splits).Append(span.ParameterEnd).ToArray();
        var pieces = ImmutableArray.CreateBuilder<RangePiece>();
        for (var i = 1; i < parameters.Length; i++)
        {
            var a = At(parameters[i - 1]); var bDepth = At(parameters[i]);
            var interval = new DepthInterval(Math.Min(a, bDepth), Math.Max(a, bDepth));
            pieces.Add(new(parameters[i - 1], parameters[i], interval, Classify(interval.Representative, participatesInClipping), Intensity(interval)));
        }
        return new(splits.ToImmutableArray(), pieces.ToImmutable());

        void Add(double threshold)
        {
            if (threshold <= Math.Min(span.DepthStart, span.DepthEnd) || threshold >= Math.Max(span.DepthStart, span.DepthEnd)) return;
            var t = span.ParameterStart + (threshold - span.DepthStart) / (span.DepthEnd - span.DepthStart) * (span.ParameterEnd - span.ParameterStart);
            if (t > span.ParameterStart && t < span.ParameterEnd) splits.Add(t);
        }
        double At(double parameter) => span.DepthStart + (parameter - span.ParameterStart) / (span.ParameterEnd - span.ParameterStart) * (span.DepthEnd - span.DepthStart);
    }

    private static void Validate(DepthInterval interval)
    {
        if (!double.IsFinite(interval.Minimum) || !double.IsFinite(interval.Maximum) || interval.Minimum > interval.Maximum)
            throw new ArgumentException("Invalid depth interval.", nameof(interval));
    }
}
