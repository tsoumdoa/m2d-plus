using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ViewSourceVolumeTests
{
    private static ViewSourceVolume Crop(Plane3? plane = null) => new(plane ?? Plane3.WorldXY, new(-1, -1, 1, 1), 10);

    [Fact] public void UnionRetainsAnyOverlapAndAnUnboundedViewDisablesScoping()
    {
        var box = new Bounds3(new(4, 0, -2), new(5, 1, -1));
        var shifted = Crop(Plane3.WorldXY.AtOrigin(new(4, 0, 0)));
        Assert.True(ViewSourceVolume.OutsideAll(box, [Crop()], .001));
        Assert.False(ViewSourceVolume.OutsideAll(box, [Crop(), shifted], .001));
        Assert.False(ViewSourceVolume.OutsideAll(box, [Crop(), new(Plane3.WorldXY, null, null)], .001));
        Assert.False(ViewSourceVolume.OutsideAll(box, [], .001));
    }

    [Fact] public void FrontAndBackLimitsRespectParticipation()
    {
        var far = new Bounds3(new(0, 0, -20), new(.5, .5, -19));
        Assert.True(Crop().Outside(far, .001));
        Assert.False(Crop().Outside(far, .001, participates: false));
        Assert.True(Crop().Outside(new(new(0, 0, 19), new(.5, .5, 20)), .001));
        Assert.False(Crop().Outside(new(new(0, 0, 19), new(.5, .5, 20)), .001, participates: false));
        Assert.True(new ViewSourceVolume(Plane3.WorldXY, null, 10).Outside(far, .001));
    }

    [Theory]
    [InlineData(1.001, false)]
    [InlineData(1.002, true)]
    public void TouchingMarginAndInvalidBoxesSurvive(double x, bool outside)
    {
        Assert.Equal(outside, Crop().Outside(new(new(x, 0, -2), new(x + 1, 1, -1)), .001));
        Assert.False(Crop().Outside(new(new(5, 5, 5), new(4, 4, 4)), .001));
        Assert.False(Crop().Outside(new(new(5, 5, 5), new(6, 6, 6)), double.NaN));
    }

    [Theory]
    [InlineData(-2, 0, -2, -1.01, .5, -1, true)]
    [InlineData(1.01, 0, -2, 2, .5, -1, true)]
    [InlineData(0, -2, -2, .5, -1.01, -1, true)]
    [InlineData(0, 1.01, -2, .5, 2, -1, true)]
    [InlineData(0, 0, -11, .5, .5, -10.01, true)]
    [InlineData(0, 0, .01, .5, .5, 1, true)]
    [InlineData(-2, 0, -2, -1, .5, -1, false)]
    [InlineData(1, 0, -2, 2, .5, -1, false)]
    [InlineData(0, -2, -2, .5, -1, -1, false)]
    [InlineData(0, 1, -2, .5, 2, -1, false)]
    [InlineData(0, 0, -11, .5, .5, -10, false)]
    [InlineData(0, 0, 0, .5, .5, 1, false)]
    [InlineData(-2, -2, -11, 2, 2, 1, false)]
    public void AllSixFacesUseStrictSeparation(double x, double y, double z, double xx, double yy, double zz, bool outside) =>
        Assert.Equal(outside, Crop().Outside(new(new(x, y, z), new(xx, yy, zz)), .001));

    [Fact] public void ProfileAppliesToNonparticipantsButDisabledFarClipKeepsBackSources()
    {
        Assert.True(Crop().Outside(new(new(2, 0, -2), new(3, .5, -1)), .001, false));
        Assert.False(new ViewSourceVolume(Plane3.WorldXY, new(-1, -1, 1, 1), null)
            .Outside(new(new(0, 0, -20), new(.5, .5, -19)), .001));
    }

    [Fact] public void RotatedWorldBoxesAreProjectedConservatively()
    {
        var plane = new Plane3(new(1e9, 1e9, 0), new(1, 1, 0), Vector3.UnitZ);
        var volume = Crop(plane);
        var center = plane.At(3, 0, -2);
        Assert.True(volume.Outside(new(center - new Vector3(.1, .1, .1), center + new Vector3(.1, .1, .1)), .001));
        var contact = plane.At(1, 0, -2);
        Assert.False(volume.Outside(new(contact, contact), .001));
    }
}
