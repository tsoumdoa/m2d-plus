using System.Collections.Immutable;

namespace M2DPlus.Core.Styling;

public enum ScaleBand { Detail, Standard, Site }
public sealed record LineStyle(double PrintWidthMillimetres, int PreviewPixels, bool Dashed, double InkIntensity);

public sealed class LineWeightPreset
{
    public double Scale { get; }
    public ScaleBand Band { get; }
    public bool UseSourceLayers { get; }
    public string BandName => Band switch { ScaleBand.Detail => "1:20–1:50", ScaleBand.Standard => "1:100", _ => "1:200–1:500" };
    public ImmutableDictionary<LineClass, LineStyle> Styles { get; }
    public LineStyle this[LineClass lineClass] => Styles[lineClass];
    public LineWeightPreset(double scale = 100, WeightMode weights = WeightMode.Auto)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!Enum.IsDefined(weights)) throw new ArgumentOutOfRangeException(nameof(weights));
        Scale = scale; UseSourceLayers = weights == WeightMode.Layers;
        // Integer edges are the UX's rounded geometric means. Fractional scales use the actual means.
        Band = scale < Math.Sqrt(50 * 100) ? ScaleBand.Detail : scale < Math.Sqrt(100 * 200) ? ScaleBand.Standard : ScaleBand.Site;
        var widths = Band switch { ScaleBand.Detail => new[] { .70, .50, .25, .18, .13 },
            ScaleBand.Standard => [.50, .35, .18, .13, .09], _ => [.35, .25, .13, .09, .05] };
        Styles = new Dictionary<LineClass, LineStyle>
        {
            [LineClass.Cut] = new(widths[0], 3, false, 1), [LineClass.Profile] = new(widths[1], 2, false, 1),
            [LineClass.Visible] = new(widths[2], 1, false, 1), [LineClass.Hidden] = new(widths[3], 1, true, 1),
            [LineClass.Beyond] = new(widths[4], 1, false, .55), [LineClass.FarClip] = new(widths[2], 1, false, 1),
            [LineClass.Hatch] = new(widths[4], 0, false, 1), [LineClass.Unresolved] = new(widths[1], 2, false, 1)
        }.ToImmutableDictionary();
    }

    /// <summary>Source-layer override wins; section BoundaryWidthScale affects only preset Cut widths.</summary>
    public double PrintWidth(LineClass lineClass, double sourceLayerWidth, double boundaryWidthScale = 1)
    {
        if (!double.IsFinite(sourceLayerWidth) || !double.IsFinite(boundaryWidthScale) || boundaryWidthScale < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLayerWidth));
        return UseSourceLayers ? sourceLayerWidth : this[lineClass].PrintWidthMillimetres * (lineClass == LineClass.Cut ? boundaryWidthScale : 1);
    }
}
