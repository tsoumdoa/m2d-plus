using M2DPlus.Core.Styling;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class LineWeightTests
{
    [Theory]
    [InlineData(5, ScaleBand.Detail, .70)] [InlineData(50, ScaleBand.Detail, .70)]
    [InlineData(70, ScaleBand.Detail, .70)] [InlineData(71, ScaleBand.Standard, .50)]
    [InlineData(75, ScaleBand.Standard, .50)] [InlineData(100, ScaleBand.Standard, .50)]
    [InlineData(141, ScaleBand.Standard, .50)] [InlineData(142, ScaleBand.Site, .35)]
    [InlineData(150, ScaleBand.Site, .35)] [InlineData(1000, ScaleBand.Site, .35)]
    public void ExactBandEdgesAndOutOfPresetScales(double scale, ScaleBand expected, double cut)
    {
        var weights = new LineWeightPreset(scale);
        Assert.Equal(expected, weights.Band); Assert.Equal(cut, weights[LineClass.Cut].PrintWidthMillimetres);
    }

    [Fact]
    public void AllClassesHaveSpecifiedWeightsFixedPreviewAndSharedEquivalences()
    {
        var preset = new LineWeightPreset();
        Assert.Equal(.35, preset[LineClass.Profile].PrintWidthMillimetres);
        Assert.Equal(.18, preset[LineClass.Visible].PrintWidthMillimetres);
        Assert.Equal(.13, preset[LineClass.Hidden].PrintWidthMillimetres);
        Assert.Equal(.09, preset[LineClass.Beyond].PrintWidthMillimetres);
        Assert.True(preset[LineClass.Hidden].Dashed); Assert.Equal(.55, preset[LineClass.Beyond].InkIntensity);
        foreach (var scale in new[] { 20d, 100, 500 })
        {
            var s = new LineWeightPreset(scale);
            Assert.Equal(s[LineClass.Visible], s[LineClass.FarClip]);
            Assert.Equal(s[LineClass.Profile], s[LineClass.Unresolved]);
            Assert.Equal(s[LineClass.Beyond].PrintWidthMillimetres, s[LineClass.Hatch].PrintWidthMillimetres);
            Assert.Equal(3, s[LineClass.Cut].PreviewPixels); Assert.Equal(2, s[LineClass.Profile].PreviewPixels);
            Assert.Equal(1, s[LineClass.Visible].PreviewPixels); Assert.Equal(0, s[LineClass.Hatch].PreviewPixels);
            Assert.Equal(8, s.Styles.Count);
        }
    }

    [Fact]
    public void SourceLayersOverrideScaleAndStyleBoundaryMultiplier()
    {
        var preset = new LineWeightPreset();
        Assert.Equal(1.25, preset.PrintWidth(LineClass.Cut, .2, 2.5));
        Assert.Equal(.18, preset.PrintWidth(LineClass.Visible, .2, 2.5));
        foreach (var scale in new[] { 20d, 200 })
        {
            var layers = new LineWeightPreset(scale, WeightMode.Layers);
            Assert.True(layers.UseSourceLayers); Assert.Equal(.2, layers.PrintWidth(LineClass.Cut, .2, 2.5));
        }
    }
}
