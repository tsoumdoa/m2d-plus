using System.Drawing;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>Preview line colours (UX §3.6): the source's display colour, ink for GH geometry and unreadable colours.</summary>
public sealed class PreviewColourTests
{
    private static readonly Color White = Color.FromArgb(255, 255, 255), Grey = Color.FromArgb(190, 190, 190),
        Dark = Color.FromArgb(38, 40, 43), Black = Color.Black, Light = Color.FromArgb(245, 245, 245);
    private static uint Argb(Color c) => unchecked((uint)c.ToArgb());

    [Fact]
    public void ObjectColourIsUsedAsIs()
    {
        var red = Color.FromArgb(255, 200, 30, 30);
        Assert.Equal(red.ToArgb(), PreviewColours.Line(Argb(red), Black, Grey).ToArgb());
        Assert.Equal(red.ToArgb(), PreviewColours.Line(Argb(red), Light, Dark).ToArgb());
    }

    [Fact]
    public void GrasshopperGeometryUsesInk()
    {
        Assert.Equal(Black, PreviewColours.Line(null, Black, Grey));
        Assert.Equal(Light, PreviewColours.Line(null, Light, Dark));
    }

    [Fact]
    public void ColoursThatWouldVanishOnTheViewportFallBackToInk()
    {
        // Rhino's default black layer colour on a dark viewport, and white objects on a white viewport.
        Assert.Equal(Light, PreviewColours.Line(Argb(Black), Light, Dark));
        Assert.Equal(Black, PreviewColours.Line(Argb(White), Black, White));
        Assert.Equal(Black.ToArgb(), PreviewColours.Line(Argb(Black), Black, Grey).ToArgb());
        // Alpha in the object colour is ignored: lines are opaque, fading is applied later.
        Assert.Equal(255, PreviewColours.Line(0x40FF0000, Black, Grey).A);
    }

    [Fact]
    public void ContrastIsWcag()
    {
        Assert.Equal(21, PreviewColours.Contrast(Black, White), 3);
        Assert.Equal(1, PreviewColours.Contrast(Grey, Grey), 6);
    }
}
