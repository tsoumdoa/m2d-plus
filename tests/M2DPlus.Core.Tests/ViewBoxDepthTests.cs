using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>Owner decision 2026-11-16: the "View box depth" prompt (unit parsing through the settings parser, the Auto
/// fill and hint) and Auto depth by view type, on plain boxes and rectangles. Expected depths are worked out by hand
/// from the boxes.</summary>
public sealed class ViewBoxDepthTests
{
    private const double Tol = .001, Mm = .001, M = 1;

    private static DepthItem Solid(double x0, double y0, double z0, double x1, double y1, double z1) => new(new(new(x0, y0, z0), new(x1, y1, z1)), true);
    private static DepthItem Curve(double x0, double y0, double z0, double x1, double y1, double z1) => new(new(new(x0, y0, z0), new(x1, y1, z1)), false);
    private static double Diagonal(double dx, double dy, double dz) => Math.Sqrt(dx * dx + dy * dy + dz * dz);

    /// <summary>A horizontal 10 × 10 frame at height <paramref name="z"/> over x, y ∈ [0, 10]. Looking down: Z up.</summary>
    private static FrontFace PlanFrame(double z, double size = 10) => new(new Plane3(new(0, 0, z), Vector3.UnitX, Vector3.UnitY), size, size);

    /// <summary>A vertical frame in the plane y = <paramref name="y"/>, x ∈ [0, w], z ∈ [0, h], looking along +Y.</summary>
    private static FrontFace NorthFrame(double y, double w = 10, double h = 6) => new(new Plane3(new(0, y, 0), Vector3.UnitX, Vector3.UnitZ), w, h);

    // ------------------------------------------------------------------ parsing (the settings unit parser)

    [Theory]
    [InlineData("8 m", Mm, 8000)] [InlineData("3000 mm", M, 3)] [InlineData(" 2.5 ", Mm, 2.5)]
    public void DepthTextUsesTheSettingsUnitParser(string text, double metresPerUnit, double expected)
    {
        Assert.True(DepthChoice.TryParse(text, metresPerUnit, Tol, out var choice, out var error), error);
        Assert.False(choice.IsAuto);
        Assert.Equal(expected, choice.Value!.Value, 9);
        Assert.Equal(Settings.ParseDistance(text, metresPerUnit), choice.Value.Value, 12);
    }

    [Theory] [InlineData(" AUTO ")]
    public void AutoTextMeansAuto(string text)
    {
        Assert.True(DepthChoice.TryParse(text, M, Tol, out var choice, out _));
        Assert.True(choice.IsAuto);
    }

    [Theory]
    [InlineData("", "Enter a depth")] [InlineData("   ", "Enter a depth")] [InlineData(null, "Enter a depth")]
    [InlineData("deep", "'deep' is not a distance")] [InlineData("5 yd", "Unknown distance unit: yd. Use mm, cm, m, km, in or ft.")] [InlineData("-3 m", "greater than 0")]
    [InlineData("0", "greater than 0")] [InlineData("0.0005", "more than the tolerance (0.001)")] [InlineData("8 m 2", "is not a distance")]
    public void InvalidDepthTextExplainsWhy(string? text, string reason)
    {
        Assert.False(DepthChoice.TryParse(text, M, Tol, out _, out var error));
        Assert.Contains(reason, error);
        Assert.True(error!.Length <= 70, $"one short line: {error}");
    }

    [Fact]
    public void AnExplicitChoiceAppliesToEveryBoxAndAutoUsesEachBoxsOwn()
    {
        var plan = new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 4);
        var section = new AutoDepth(12_345, ViewBoxKind.Section, AutoDepthRule.Back, 9);
        Assert.Equal(3000, DepthChoice.Auto.Resolve(plan));
        Assert.Equal(12_345, DepthChoice.Auto.Resolve(section));
        Assert.Equal(8000, DepthChoice.Of(8000).Resolve(plan));
        Assert.Equal(8000, DepthChoice.Of(8000).Resolve(section));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthChoice.Of(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthChoice.Of(double.NaN));
    }

    // ------------------------------------------------------------------ plans

    [Fact]
    public void PlanInAMillimetreFileIsOneStoreyBelowTheCut()
    {
        // Cut at 1,200 mm through a 3,000 mm wall; the floor slab (−300…0) is 1,200 mm below: within one storey.
        var auto = ViewBoxDepth.Auto(PlanFrame(1200, 10_000), [Solid(1000, 1000, 0, 1200, 9000, 3000), Solid(0, 0, -300, 10_000, 10_000, 0)], Tol, Mm);
        Assert.Equal(new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 2), auto);
        Assert.Equal(3000, ViewBoxDepth.Storey(Mm), 12);
        Assert.Equal(3, ViewBoxDepth.Storey(M), 12);
        Assert.Equal(3 / .3048, ViewBoxDepth.Storey(.3048), 12);
    }

    [Fact]
    public void PlanWithNothingWithinOneStoreyExtendsToTheBackOfWhatIsInsideTheFrame()
    {
        // A cut 6 m above the floor of a tall hall: only the cut walls are within a storey; the floor (−0.3…0) is 6 m down.
        var items = new[] { Solid(1, 1, 0, 1.2, 9, 9), Solid(0, 0, -.3, 10, 10, 0) };
        var auto = ViewBoxDepth.Auto(PlanFrame(6), items, Tol, M);
        Assert.Equal(ViewBoxKind.Plan, auto.Kind);
        Assert.Equal(AutoDepthRule.StoreyExtended, auto.Rule);
        Assert.Equal(6.3 + Diagonal(10, 10, 9.3) * .01, auto.Depth, 9);

        // Walls alone, no floor: the back (1.2 m) is above one storey, so the depth stays one storey.
        var walls = ViewBoxDepth.Auto(PlanFrame(1.2), [Solid(1, 1, 0, 1.2, 9, 3)], Tol, M);
        Assert.Equal(new AutoDepth(3, ViewBoxKind.Plan, AutoDepthRule.StoreyExtended, 1), walls);
    }

    [Fact]
    public void ObjectsBesideTheFrameOrInFrontOfItNeverCount()
    {
        // Within the storey but outside the frame's rectangle (x > 10): not counted, so the plan extends to the floor.
        var items = new[] { Solid(1, 1, -20, 1.2, 9, 3), Solid(0, 0, -20.3, 10, 10, -20), Solid(12, 0, -1, 20, 10, 0), Solid(0, 0, 4, 10, 10, 5) };
        var auto = ViewBoxDepth.Auto(PlanFrame(1.2), items, Tol, M);
        Assert.Equal(AutoDepthRule.StoreyExtended, auto.Rule);
        Assert.Equal(2, auto.Inside);
        Assert.Equal(21.5 + Diagonal(10, 10, 23.3) * .01, auto.Depth, 9);
    }

    // ------------------------------------------------------------------ sections and elevations

    [Fact]
    public void SectionGoesToTheBackOfTheGeometryInsideTheFrameOnly()
    {
        // Frame y = 5 looking north (+Y) over x ∈ [0, 10], z ∈ [0, 6]. A building y ∈ [0, 20] is cut; a tower far behind
        // but beside the frame (x 30…40) and anything in front of the frame (y < 5) do not count.
        var frame = NorthFrame(5);
        var items = new[] { Solid(0, 0, 0, 10, 20, 6), Solid(30, 0, 0, 40, 300, 50), Solid(0, -50, 0, 10, -40, 6) };
        var auto = ViewBoxDepth.Auto(frame, items, Tol, M);
        Assert.Equal(ViewBoxKind.Section, auto.Kind);
        Assert.Equal(AutoDepthRule.Back, auto.Rule);
        Assert.Equal(1, auto.Inside);
        Assert.Equal(15 + Diagonal(10, 20, 6) * .01, auto.Depth, 9);
    }

    [Fact]
    public void ElevationGoesToTheBackOfTheGeometryInsideTheFrame()
    {
        var frame = NorthFrame(-2);
        var auto = ViewBoxDepth.Auto(frame, [Solid(0, 0, 0, 10, 20, 6), Solid(2, 25, 0, 4, 27, 1), Solid(50, 0, 0, 60, 100, 6)], Tol, M);
        Assert.Equal(new AutoDepth(29 + Diagonal(10, 27, 6) * .01, ViewBoxKind.Elevation, AutoDepthRule.Back, 2), auto);
        // A horizontal frame above everything (a roof plan) cuts nothing: an elevation, not one storey.
        var roof = ViewBoxDepth.Auto(PlanFrame(10), [Solid(0, 0, 0, 10, 10, 6)], Tol, M);
        Assert.Equal(new AutoDepth(10 + Diagonal(10, 10, 6) * .01, ViewBoxKind.Elevation, AutoDepthRule.Back, 1), roof);
    }

    [Fact]
    public void ARisingPlanAndACurveThroughTheFrameAreNotPlans()
    {
        // A horizontal frame looking up (Z down) that cuts is a section (a reflected ceiling plan), not one storey.
        var up = new FrontFace(new Plane3(new(10, 0, 1), -Vector3.UnitX, Vector3.UnitY), 10, 10);
        var auto = ViewBoxDepth.Auto(up, [Solid(0, 0, 0, 10, 10, 3)], Tol, M);
        Assert.Equal(ViewBoxKind.Section, auto.Kind);
        Assert.Equal(2 + Diagonal(10, 10, 3) * .01, auto.Depth, 9);
        // Curves are not cut by the component, so a curve through the plane makes no plan.
        var curve = ViewBoxDepth.Auto(PlanFrame(1), [Curve(5, 5, -4, 5, 5, 4)], Tol, M);
        Assert.Equal(ViewBoxKind.Elevation, curve.Kind);
        Assert.Equal(5 + Math.Max(10 * Tol, 8 * .01), curve.Depth, 9);
    }

    [Fact]
    public void NothingInsideTheFrameFallsBackToItsLongerSide()
    {
        Assert.Equal(new AutoDepth(10, ViewBoxKind.Elevation, AutoDepthRule.FrameSide, 0), ViewBoxDepth.Auto(NorthFrame(5, 10, 6), [], Tol, M));
        Assert.Equal(new AutoDepth(8, ViewBoxKind.Elevation, AutoDepthRule.FrameSide, 0),
            ViewBoxDepth.Auto(NorthFrame(5, 4, 8), [Solid(20, 0, 0, 30, 50, 6), Solid(0, -9, 0, 4, 4, 6)], Tol, M));
    }

    [Fact]
    public void SearchBoundsHoldEverythingThatCanBeInsideTheFrame()
    {
        var frame = NorthFrame(5);
        var scene = new Bounds3(new(-100, -100, -10), new(100, 400, 60));
        var search = ViewBoxDepth.SearchBounds(frame, scene, Tol)!.Value;
        Assert.Equal((-Tol, 5 - Tol, -Tol), (search.Min.X, search.Min.Y, search.Min.Z));
        Assert.Equal((10 + Tol, 400 + Tol, 6 + Tol), (search.Max.X, search.Max.Y, search.Max.Z));
        // Nothing behind the frame: no search at all.
        Assert.Null(ViewBoxDepth.SearchBounds(NorthFrame(500), scene, Tol));
        // An inclined frame: the prism's box covers every corner swept back to the scene's far side.
        var tilted = new FrontFace(new Plane3(new(0, 0, 0), Vector3.UnitX, new Vector3(0, 1, 1)), 4, 4);
        var reach = ViewBoxDepth.SearchBounds(tilted, scene, Tol)!.Value;
        Assert.True(reach.Min.Z <= -10 + Tol && reach.Max.Y >= 4 / Math.Sqrt(2) - Tol);
    }

    // ------------------------------------------------------------------ the dialog model

    [Fact]
    public void ThePromptFillsTheAutoValueWithUnitsAndAcceptsItAsAuto()
    {
        var prompt = new ViewDepthPrompt([new AutoDepth(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 3)], Mm, Tol);
        Assert.Equal("View box depth", prompt.Title);
        Assert.Equal("3000 mm", prompt.AutoText);
        Assert.Equal("Auto: plans 3 m below the cut.", prompt.Hint);
        Assert.Equal("Depth", prompt.DepthLabel);
        Assert.True(prompt.TryAccept(" 3000 MM ", out var auto, out _));
        Assert.True(auto.IsAuto);
        Assert.True(prompt.TryAccept("8 m", out var eight, out _));
        Assert.Equal(8000, eight.Value);
        Assert.False(prompt.TryAccept("8 parsecs", out _, out var error));
        Assert.Contains("Unknown distance unit: parsecs", error);
        Assert.Equal("12.438 m", new ViewDepthPrompt([new AutoDepth(12.43781, ViewBoxKind.Section, AutoDepthRule.Back, 1)], M, Tol).AutoText);
        Assert.Equal("12.5", new ViewDepthPrompt([new AutoDepth(12.5, ViewBoxKind.Section, AutoDepthRule.Back, 1)], .9144, Tol).AutoText); // yards: bare
    }

    [Fact]
    public void SeveralBoxesShareOneDepthAndTheHintSaysWhatAutoMeansForThem()
    {
        AutoDepth[] mixed = [new(3000, ViewBoxKind.Plan, AutoDepthRule.Storey, 3), new(25_000, ViewBoxKind.Section, AutoDepthRule.Back, 7),
            new(40_000, ViewBoxKind.Elevation, AutoDepthRule.Back, 9)];
        var prompt = new ViewDepthPrompt(mixed, Mm, Tol);
        Assert.Equal("auto", prompt.AutoText);
        Assert.Equal("Depth for all 3", prompt.DepthLabel);
        Assert.Equal("Auto: plans 3 m below the cut; sections and elevations to the back of what's inside the frame.", prompt.Hint);
        Assert.True(prompt.TryAccept("auto", out var choice, out _) && choice.IsAuto);
        Assert.Equal("3000 mm", new ViewDepthPrompt([mixed[0], mixed[0]], Mm, Tol).AutoText);

        Assert.Equal("Auto: elevations to the back of what's inside the frame.", ViewDepthPrompt.HintFor([mixed[2]]));
        Assert.Equal("Auto: nothing is inside the frame, so its longer side.",
            ViewDepthPrompt.HintFor([new AutoDepth(10, ViewBoxKind.Elevation, AutoDepthRule.FrameSide, 0)]));
        Assert.Equal("Auto: plans 3 m below the cut, more if nothing is there; empty frames their longer side.",
            ViewDepthPrompt.HintFor([mixed[0], new(9, ViewBoxKind.Plan, AutoDepthRule.StoreyExtended, 2), new(10, ViewBoxKind.Elevation, AutoDepthRule.FrameSide, 0)]));
        Assert.Throws<ArgumentException>(() => new ViewDepthPrompt([], M, Tol));
    }
}
