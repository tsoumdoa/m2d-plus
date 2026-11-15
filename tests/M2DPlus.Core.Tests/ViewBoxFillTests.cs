using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>Owner decision 2026-11-15: the front-face fill of view boxes (colour parsing, by type, override) and the
/// cut hint used to pick the type.</summary>
public sealed class ViewBoxFillTests
{
    private const double Tol = .001;

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("default")] [InlineData(" Default ")] [InlineData("DEFAULT")]
    public void BlankAndDefaultMeanByType(string? text)
    {
        Assert.True(ViewBoxFill.TryParse(text, out var r, out _, out _, out var a));
        Assert.Null(r); Assert.Null(a);
        Assert.Equal(new FillChoice(ViewBoxFill.Cut, true, FillSource.ByType, null), ViewBoxFill.Choose(true, text));
        Assert.Equal(new FillChoice(ViewBoxFill.NoCut, false, FillSource.ByType, null), ViewBoxFill.Choose(false, text));
        Assert.Null(ViewBoxFill.Validate(text, "A"));
    }

    [Fact]
    public void RgbKeepsTheByTypeOpacityAndRgbaSetsIt()
    {
        var rgb = ViewBoxFill.Choose(true, " #00ff80 ");
        Assert.Equal(new Rgba(0x00, 0xFF, 0x80, ViewBoxFill.Cut.A), rgb.Colour);
        Assert.Equal(FillSource.Override, rgb.Source);
        Assert.Equal(ViewBoxFill.NoCut.A, ViewBoxFill.Choose(false, "#00FF80").Colour.A);
        var rgba = ViewBoxFill.Choose(false, "#12aBcD7f");
        Assert.Equal(new Rgba(0x12, 0xAB, 0xCD, 0x7F), rgba.Colour);
        Assert.Equal("#12ABCD7F", rgba.Colour.Hex);
        Assert.False(rgba.Cuts);
        Assert.Equal(new Rgba(1, 2, 3, 255), ViewBoxFill.Choose(true, "#010203FF").Colour);
    }

    [Theory]
    [InlineData("red")] [InlineData("#12345")] [InlineData("#1234567")] [InlineData("#GG0000")] [InlineData("00FF80")]
    [InlineData("#00FF80FF00")] [InlineData("# 00FF80")] [InlineData("#+1FF80")] [InlineData("255,0,0")] [InlineData("by type")]
    public void InvalidValuesFallBackToByTypeWithAProblem(string text)
    {
        Assert.False(ViewBoxFill.TryParse(text, out _, out _, out _, out _));
        var choice = ViewBoxFill.Choose(true, text, "Section A");
        Assert.Equal(ViewBoxFill.Cut, choice.Colour);
        Assert.Equal(FillSource.Invalid, choice.Source);
        Assert.Contains("Section A", choice.Problem);
        Assert.Contains(text.Trim(), choice.Problem);
        Assert.Contains("#RRGGBB", choice.Problem);
        Assert.Equal(choice.Problem, ViewBoxFill.Validate(text, "Section A"));
        Assert.Equal(ViewBoxFill.NoCut, ViewBoxFill.Choose(false, text).Colour);
    }

    [Fact]
    public void ByTypeColoursAreTheOwnersHuesAtTwentyToThirtyPercent()
    {
        Assert.Equal("#D9412B", (ViewBoxFill.Cut with { A = 255 }).Hex);
        Assert.Equal("#1F6FD1", (ViewBoxFill.NoCut with { A = 255 }).Hex);
        Assert.InRange(ViewBoxFill.Cut.Opacity, .2, .3);
        Assert.InRange(ViewBoxFill.NoCut.Opacity, .2, .3);
    }

    // ------------------------------------------------------------------ cut hint

    private static Vector3 V(double x, double y, double z) => new(x, y, z);
    private static Bounds3 Box(double x0, double y0, double z0, double x1, double y1, double z1) => new(V(x0, y0, z0), V(x1, y1, z1));
    /// <summary>A plan frame at height <paramref name="z"/>: x 0–10, y 0–6, looking down.</summary>
    private static FrontFace Plan(double z) => new(new Plane3(V(0, 0, z), Vector3.UnitX, Vector3.UnitY), 10, 6);
    /// <summary>A vertical frame at y = 0 looking +Y (Z toward −Y): x 0–10, z 0–4.</summary>
    private static FrontFace Section() => new(new Plane3(V(0, 0, 0), Vector3.UnitX, Vector3.UnitZ), 10, 4);

    [Fact]
    public void ABoxStraddlingThePlaneInsideTheFrameCuts()
    {
        var wall = Box(2, 1, 0, 3, 5, 3);
        Assert.True(CutHint.Cuts(Plan(1.5), wall, Tol));
        Assert.False(CutHint.Cuts(Plan(4), wall, Tol)); // above it: an elevation-style view
        Assert.False(CutHint.Cuts(Plan(-1), wall, Tol)); // below it
        Assert.True(CutHint.Cuts(Section(), Box(1, -1, 0, 2, 1, 2), Tol));
        Assert.False(CutHint.Cuts(Section(), Box(1, 1, 0, 2, 3, 2), Tol)); // all behind the frame
    }

    [Fact]
    public void TouchingThePlaneWithinToleranceIsNotACut()
    {
        // A slab whose top lies on the plan plane is not cut (faces on the cut plane draw as lines), nor one that
        // reaches past it by less than the tolerance; a straddle beyond the tolerance is.
        Assert.False(CutHint.Cuts(Plan(1), Box(0, 0, 0, 10, 6, 1), Tol));
        Assert.False(CutHint.Cuts(Plan(1), Box(0, 0, 0, 10, 6, 1 + Tol / 2), Tol));
        Assert.False(CutHint.Cuts(Plan(1), Box(0, 0, 1 - Tol / 2, 10, 6, 2), Tol));
        Assert.True(CutHint.Cuts(Plan(1), Box(0, 0, 0, 10, 6, 1 + 2 * Tol), Tol));
    }

    [Fact]
    public void AStraddleOutsideTheFrameRectangleIsNotACut()
    {
        Assert.False(CutHint.Cuts(Plan(1), Box(11, 1, 0, 12, 2, 2), Tol)); // beside the frame in x
        Assert.False(CutHint.Cuts(Plan(1), Box(1, -3, 0, 2, -1, 2), Tol)); // beside it in y
        Assert.False(CutHint.Cuts(Plan(1), Box(10, 1, 0, 12, 2, 2), Tol)); // only touching the frame edge
        Assert.True(CutHint.Cuts(Plan(1), Box(9.9, 1, 0, 12, 2, 2), Tol)); // overlapping it by 0.1
        Assert.False(CutHint.Cuts(Section(), Box(1, -1, 5, 2, 1, 6), Tol)); // above a section frame
        Assert.True(CutHint.Cuts(Plan(1), Box(-100, -100, -1, 100, 100, 3), Tol)); // a site box around everything
    }

    [Fact]
    public void InclinedFramesUseTheProjectedBoundingBox()
    {
        // A frame tilted 45° about X through the origin: X = world X, Y = (0, 1, 1)/√2, width 4, height 4.
        var tilted = new FrontFace(new Plane3(V(0, 0, 0), Vector3.UnitX, V(0, 1, 1)), 4, 4);
        Assert.True(CutHint.Cuts(tilted, Box(1, .5, .5, 2, 1.5, 1.5), Tol)); // centre on the plane, inside
        Assert.False(CutHint.Cuts(tilted, Box(1, -3, 2, 2, -2, 3), Tol)); // in front of it
        Assert.False(CutHint.Cuts(tilted, Box(1, 10, 10, 2, 11, 11), Tol)); // on the plane, past the frame's top
    }

    [Fact]
    public void TheSearchBoundsContainEveryCutter()
    {
        var faces = new[] { Plan(1), Section(), new FrontFace(new Plane3(V(3, -2, 1), V(1, 1, 0), V(-1, 1, 2)), 5, 3) };
        var random = new Random(20261115);
        foreach (var face in faces)
        {
            var search = CutHint.SearchBounds(face, Tol);
            var cutters = 0;
            for (var i = 0; i < 4000; i++)
            {
                var (x, y, z) = (random.NextDouble() * 24 - 12, random.NextDouble() * 24 - 12, random.NextDouble() * 24 - 12);
                var box = Box(x, y, z, x + random.NextDouble() * 4, y + random.NextDouble() * 4, z + random.NextDouble() * 4);
                if (!CutHint.Cuts(face, box, Tol)) continue;
                cutters++;
                Assert.True(box.Min.X <= search.Max.X && box.Max.X >= search.Min.X && box.Min.Y <= search.Max.Y &&
                    box.Max.Y >= search.Min.Y && box.Min.Z <= search.Max.Z && box.Max.Z >= search.Min.Z, $"{box} outside {search}");
            }
            Assert.True(cutters > 20, $"only {cutters} cutters sampled");
        }
    }

    [Fact]
    public void AnyCutsIsTheFirstCutterOverASet()
    {
        Assert.False(CutHint.AnyCuts(Plan(5), [Box(0, 0, 0, 1, 1, 1), Box(2, 2, 0, 3, 3, 4)], Tol));
        Assert.True(CutHint.AnyCuts(Plan(3), [Box(0, 0, 0, 1, 1, 1), Box(2, 2, 0, 3, 3, 4)], Tol));
        Assert.False(CutHint.AnyCuts(Plan(3), [], Tol));
    }
}
