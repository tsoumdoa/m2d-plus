using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ViewSourceBoundsTests
{
    private static Frame2 Crop => new(-1, -1, 1, 1);

    [Theory]
    [InlineData(1.002, true)]
    [InlineData(1.001, false)]
    [InlineData(1.0005, false)]
    [InlineData(1, false)]
    [InlineData(.9995, false)]
    public void CropSeparationReservesTheEntireMargin(double x, bool excluded)
    {
        Assert.Equal(excluded, ViewSourceBounds.Outside(new(new(x, 0, -2), new(x + 1, .5, -1)), Crop, null, .001));
        Assert.Equal(excluded, ViewSourceBounds.Outside(new(new(-x - 1, 0, -2), new(-x, .5, -1)), Crop, null, .001));
        Assert.Equal(excluded, ViewSourceBounds.Outside(new(new(0, x, -2), new(.5, x + 1, -1)), Crop, null, .001));
        Assert.Equal(excluded, ViewSourceBounds.Outside(new(new(0, -x - 1, -2), new(.5, -x, -1)), Crop, null, .001));
    }

    [Theory]
    [InlineData(-10.002, true)]
    [InlineData(-10.001, false)]
    [InlineData(-10.0005, false)]
    [InlineData(-10, false)]
    [InlineData(10, false)]
    public void FarSeparationRetainsContactAndForeground(double z, bool excluded) =>
        Assert.Equal(excluded, ViewSourceBounds.Outside(new(new(0, 0, z - 1), new(.5, .5, z)), null, 10, .001));

    [Fact] public void DisabledLimitsInvalidBoundsAndLargeCoordinateArithmeticRetainSources()
    {
        var outside = new Bounds3(new(4, 4, -20), new(5, 5, -19));
        Assert.False(ViewSourceBounds.Outside(outside, null, null, .001));
        Assert.False(ViewSourceBounds.Outside(new(new(1, 1, 1), new(0, 0, 0)), Crop, 10, .001));
        Assert.False(ViewSourceBounds.Outside(outside, Crop, 10, double.NaN));
        double margin = ViewSourceBounds.Margin(.001, 1e12);
        Assert.True(margin > .02);
        Assert.False(ViewSourceBounds.Outside(new(new(1.02, 0, -2), new(2, 1, -1)), Crop, null, margin));
    }
}
