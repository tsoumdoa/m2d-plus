using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ViewRangeTests
{
    [Fact]
    public void BeyondSplitUsesExactParameterAndMovesRatherThanCopies()
    {
        var result = new ViewRange(20, 3).Partition(new(10, 30, 1, 5));
        Assert.Equal(20, Assert.Single(result.SplitParameters));
        Assert.Equal(2, result.Pieces.Length);
        Assert.Equal(RangeClassification.Visible, result.Pieces[0].Classification);
        Assert.Equal(RangeClassification.Beyond, result.Pieces[1].Classification);
        Assert.Equal(10, result.Pieces[0].ParameterStart); Assert.Equal(20, result.Pieces[0].ParameterEnd);
        Assert.Equal(20, result.Pieces[1].ParameterStart); Assert.Equal(30, result.Pieces[1].ParameterEnd);
        Assert.Equal(2, result.Pieces[0].Depths.Representative); Assert.Equal(4, result.Pieces[1].Depths.Representative);
        Assert.Equal(20, result.Pieces.Sum(p => p.ParameterEnd - p.ParameterStart));
    }

    [Fact]
    public void DescendingSpanPreservesParameterOrderAndThresholdBelongsToVisible()
    {
        var range = new ViewRange(20, 3);
        var split = range.Partition(new(0, 1, 5, 1));
        Assert.Equal(.5, Assert.Single(split.SplitParameters));
        Assert.Equal(RangeClassification.Beyond, split.Pieces[0].Classification);
        Assert.Equal(RangeClassification.Visible, split.Pieces[1].Classification);
        Assert.Equal(RangeClassification.Visible, range.Classify(3));
        Assert.Equal(RangeClassification.Beyond, range.Classify(double.BitIncrement(3)));
    }

    [Theory]
    [InlineData(0, 3, RangeClassification.Visible)]
    [InlineData(3, 6, RangeClassification.Beyond)]
    [InlineData(3, 3, RangeClassification.Visible)]
    public void EndpointThresholdDoesNotProduceZeroLengthPieces(double a, double b, RangeClassification expected)
    {
        var split = new ViewRange(10, 3).Partition(new(2, 4, a, b));
        Assert.Empty(split.SplitParameters); Assert.Equal(expected, Assert.Single(split.Pieces).Classification);
    }

    [Fact]
    public void NearBeyondFarProduceCompleteDisjointPartition()
    {
        var result = new ViewRange(10, 3, FarClipMode.Line).Partition(new(0, 14, -2, 12));
        Assert.Equal(new[] { 2d, 5, 12 }, result.SplitParameters);
        Assert.Equal(new[] { RangeClassification.Clipped, RangeClassification.Visible, RangeClassification.Beyond, RangeClassification.Clipped },
            result.Pieces.Select(p => p.Classification));
        Assert.Equal(14, result.Pieces.Sum(p => p.ParameterEnd - p.ParameterStart));
    }

    [Fact]
    public void NoClipRetainsDeeperGeometryAndBeyondEvenBeyondDepth()
    {
        var range = new ViewRange(3, 4, FarClipMode.None);
        Assert.False(range.BeyondIgnored); Assert.False(range.GenerateFarClipLines); Assert.False(range.ClipsFar);
        Assert.Equal(RangeClassification.Beyond, range.Classify(10));
        Assert.Equal(1d / 3, Assert.Single(range.Partition(new(0, 1, 2, 8)).SplitParameters), 12);
        Assert.True(new ViewRange(3, 4).BeyondIgnored);
    }

    [Fact]
    public void LineAndNoLineShareTrimButOnlyLineRequestsIndependentFarContours()
    {
        var line = new ViewRange(10, farClip: FarClipMode.Line);
        var noLine = new ViewRange(10);
        Assert.True(line.GenerateFarClipLines); Assert.False(noLine.GenerateFarClipLines);
        Assert.True(line.ClipsFar); Assert.True(noLine.ClipsFar);
        Assert.Equal(line.Classify(11), noLine.Classify(11));
        Assert.Equal(RangeClassification.Visible, line.Classify(10));
    }

    [Theory]
    [InlineData(0, DepthRegime.SectionOnly, RangeClassification.SectionOnly)]
    [InlineData(.0005, DepthRegime.UncertainThinVolume, RangeClassification.Uncertain)]
    [InlineData(.001, DepthRegime.UncertainThinVolume, RangeClassification.Uncertain)]
    [InlineData(.002, DepthRegime.Volume, RangeClassification.Visible)]
    public void S2KeepsZeroSeparateFromThinPositiveDepth(double depth, DepthRegime regime, RangeClassification classification)
    {
        var range = new ViewRange(depth, farClip: FarClipMode.Line, fade: true);
        Assert.Equal(regime, range.Regime); Assert.Equal(classification, range.Classify(depth / 2));
        Assert.Equal(regime == DepthRegime.Volume, range.GenerateFarClipLines);
        Assert.True(double.IsFinite(range.Intensity(0)));
        Assert.Equal(RangeClassification.Visible, range.Classify(9, false));
    }

    [Fact]
    public void OutOfScopeSourcesRemainWholeAcrossNearAndFarPlanes()
    {
        var split = new ViewRange(2).Partition(new(0, 1, -3, 5), false);
        Assert.Empty(split.SplitParameters); Assert.Equal(RangeClassification.Visible, Assert.Single(split.Pieces).Classification);
        Assert.Equal(DepthRegime.Volume, new ViewRange(0, farClip: FarClipMode.None).Regime);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(3, .8125)] [InlineData(5, .625)]
    [InlineData(9, .25)] [InlineData(10, .25)] [InlineData(25, .25)] [InlineData(-1, 1)]
    public void RevitFadeIsLinearBetweenStartEndAndClamped(double depth, double expected)
    {
        var range = new ViewRange(10, fade: true, fadeStart: 10, fadeEnd: 90, fadeLimit: 25);
        Assert.Equal(expected, range.Intensity(depth), 12);
    }

    [Fact]
    public void FadingUsesMidDepthDoesNotSplitAndSkipsCutHatchUnresolved()
    {
        var range = new ViewRange(10, fade: true, fadeLimit: 30);
        var split = range.Partition(new(0, 1, 0, 10));
        Assert.Empty(split.SplitParameters); Assert.Equal(.65, Assert.Single(split.Pieces).Intensity, 12);
        Assert.Equal(.65, range.Intensity(new DepthInterval(2, 8)), 12);
        foreach (var c in new[] { LineClass.Cut, LineClass.Hatch, LineClass.Unresolved }) Assert.Equal(1, range.Intensity(10, c));
        foreach (var c in new[] { LineClass.Visible, LineClass.Profile, LineClass.Beyond, LineClass.Hidden, LineClass.FarClip })
            Assert.Equal(.3, range.Intensity(10, c), 12);
        Assert.Equal(1, new ViewRange(10).Intensity(10));
    }

    [Fact]
    public void EqualFadeEndpointsAreStepAndZeroReferenceNeverDividesByZero()
    {
        var step = new ViewRange(10, fade: true, fadeStart: 50, fadeEnd: 50, fadeLimit: 0);
        Assert.Equal(1, step.Intensity(4.999)); Assert.Equal(0, step.Intensity(5));
        Assert.Equal(1, new ViewRange(0, fade: true).Intensity(100));
        Assert.Equal(.35, ViewRange.QuantizeForBake(.326), 12); Assert.Equal(.3, ViewRange.QuantizeForBake(.324), 12);
    }

    [Fact]
    public void InvalidArithmeticInputsFailExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRange(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRange(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRange(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRange(1, fadeStart: 60, fadeEnd: 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewRange(1, fadeLimit: double.NaN));
        Assert.Throws<ArgumentException>(() => new ViewRange(1).Partition(new(1, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => new ViewRange(1).Intensity(new DepthInterval(2, 1)));
    }
}
