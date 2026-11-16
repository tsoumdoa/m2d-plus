using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class OffsetPresentationTests(RhinoHost host, ITestOutputHelper output)
{
    // The radius-200 circular column from plan 7 of the ten-plan offset fixture.
    // Its analytic loop is reparameterized by length, as the section result is.
    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e5)]
    [InlineData(1e6)]
    public void SurveyPlanPresentationKeepsTheAnalyticCircleAndItsProvenance(double offset) => host.Run(() =>
    {
        var plane = new Plane(new Point3d(offset, offset, offset + 23900), Vector3d.ZAxis);
        var centre = new Point3d(offset + 32200, offset + 7000, offset + 23900);
        var loop = new ArcCurve(new Circle(new Plane(centre, Vector3d.ZAxis), 200)) { Domain = new(0, 400 * Math.PI) };
        var region = new SectionRegion("column", [new(loop, false, [new(loop.DuplicateCurve(), EdgeOrigin.PhysicalCut)])],
            null, ["column"], SectionStyleSnapshot.Default(), SectionStatus.Crossing, Math.PI * 40000, null);
        var drawing = new ViewDrawing { Frame = new(plane, new Interval(0, 36000), new Interval(0, 17000)), Tolerance = .01 };
        drawing.Regions.Add(new(region, [0]));
        using var raw = new DrawingBatch(new("offset", "plan7", "normal", 1), [drawing]);
        var before = WorkerCodec.CaptureDrawing(drawing, true);
        using var result = DrawingPresentation.Apply(raw, raw.Identity, [ViewRequest.Parse(plane)], []);
        var view = Assert.Single(result.Views); Assert.Null(view.Error);
        var shown = Assert.Single(view.Regions).Region;
        Assert.NotNull(shown.Hatch); Assert.Single(shown.Loops); Assert.False(shown.Loops[0].IsHole);
        Assert.InRange(Math.Abs(shown.Area - Math.PI * 40000), 0, .01);
        Assert.All(shown.Loops.SelectMany(l => l.Edges), e => Assert.Equal(EdgeOrigin.PhysicalCut, e.Origin));
        Assert.Equal(before, WorkerCodec.CaptureDrawing(drawing, true));
        output.WriteLine(view.Info);
    });

    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e6)]
    public void UncertifiablePresentationHasUnresolvedInkAndKeepsItsProvenNeighbour(double offset) => host.Run(() =>
    {
        var plane = new Plane(new Point3d(offset, offset, offset + 23900), Vector3d.ZAxis);
        var square = new Rectangle3d(plane, 400, 400).ToNurbsCurve();
        var open = new LineCurve(plane.PointAt(1000, 0), plane.PointAt(1100, 0));
        SectionRegion Region(string id, Curve curve, double area) => new(id,
            [new(curve, false, [new(curve.DuplicateCurve(), EdgeOrigin.PhysicalCut)])], null, [id],
            SectionStyleSnapshot.Default(), SectionStatus.Crossing, area, null);
        var drawing = new ViewDrawing { Frame = new(plane, 2000, 1000), Tolerance = .01 };
        drawing.Regions.Add(new(Region("proven", square, 160000), [0]));
        drawing.Regions.Add(new(Region("refused", open, 0), [1]));
        using var raw = new DrawingBatch(new("offset", "refusal", "normal", 1), [drawing]);
        var before = WorkerCodec.CaptureDrawing(drawing, true);
        using var presented = DrawingPresentation.Apply(raw, raw.Identity, [ViewRequest.Parse(plane)], []);
        var view = Assert.Single(presented.Views); Assert.Null(view.Error);
        var neighbour = Assert.Single(view.Regions).Region;
        Assert.Equal("proven", Assert.Single(neighbour.SourceIds)); Assert.NotNull(neighbour.Hatch);
        Assert.InRange(Math.Abs(neighbour.Area - 160000), 0, .001);
        var marker = Assert.Single(view.Items); Assert.Equal("refused", marker.SourceIdentity);
        Assert.Equal(LineClass.Unresolved, marker.Class); Assert.Equal(LineClass.Unresolved, marker.SolvedClass);
        Assert.Equal(1, view.Report.UncertainCount); Assert.Equal(1, view.UncertainSectionComponents);
        Assert.Contains(view.Diagnostics, d => d.Contains("Unresolved section presentation", StringComparison.Ordinal));
        Assert.Equal(before, WorkerCodec.CaptureDrawing(drawing, true));
    });

    [RhinoTheory]
    [InlineData(0d)]
    [InlineData(1e6)]
    public void LocalPresentationPreservesHolesAndPlaneRelativePatternOrigin(double offset) => host.Run(() =>
    {
        var plane = new Plane(new Point3d(offset, offset, offset + 23900), Vector3d.ZAxis);
        SectionLoop Loop(double radius, bool hole)
        {
            var curve = new ArcCurve(new Circle(plane, radius)) { Domain = new(0, 2 * Math.PI * radius) };
            return new(curve, hole, [new(curve.DuplicateCurve(), EdgeOrigin.PhysicalCut)]);
        }
        var style = SectionStyleSnapshot.Default() with { HasStyle = true,
            Pattern = new("lines", false, [new(0, new(0, 0), new(0, 10), [])]), PatternOrigin = new(123, 45) };
        var region = new SectionRegion("ring", [Loop(200, false), Loop(100, true)], null, ["ring"], style,
            SectionStatus.Crossing, Math.PI * 30000, null);
        var drawing = new ViewDrawing { Frame = new(plane, new Interval(-300, 300), new Interval(-300, 300)), Tolerance = .01 };
        drawing.Regions.Add(new(region, [0]));
        using var raw = new DrawingBatch(new("offset", "ring", "normal", 1), [drawing]);
        var target = new Plane(new Point3d(2000, 3000, 0), Vector3d.ZAxis);
        using var presented = DrawingPresentation.Apply(raw, raw.Identity, [ViewRequest.Parse(plane)], [target]);
        var view = Assert.Single(presented.Views); Assert.Empty(view.Items);
        var shown = Assert.Single(view.Regions).Region;
        Assert.Equal(2, shown.Loops.Count); Assert.Single(shown.Loops, l => l.IsHole);
        Assert.InRange(Math.Abs(shown.Area - Math.PI * 30000), 0, .01);
        Assert.Equal(style.PatternOrigin, shown.Style.PatternOrigin);
        Assert.NotNull(shown.Hatch);
        Assert.InRange(shown.Hatch.BasePoint.DistanceTo(view.Frame.Plane.PointAt(123, 45)), 0, 1e-7);
    });

    [RhinoFact]
    public void PresentationCannotAuthorizeWorldPrecisionLostBeforeTranslation() => host.Run(() =>
    {
        var plane = new Plane(new Point3d(1e15, 1e15, 1e15), Vector3d.ZAxis);
        var curve = new ArcCurve(new Circle(plane, 200));
        var drawing = new ViewDrawing { Frame = new(plane, 1000, 1000), Tolerance = .01 };
        drawing.Regions.Add(new(new("far", [new(curve, false, [new(curve.DuplicateCurve(), EdgeOrigin.PhysicalCut)])],
            null, ["far"], SectionStyleSnapshot.Default(), SectionStatus.Crossing, Math.PI * 40000, null), [0]));
        using var raw = new DrawingBatch(new("far", "plan", "normal", 1), [drawing]);
        using var presented = DrawingPresentation.Apply(raw, raw.Identity, [ViewRequest.Parse(plane)], []);
        var view = Assert.Single(presented.Views); Assert.Null(view.Error); Assert.Empty(view.Regions);
        Assert.Equal(LineClass.Unresolved, Assert.Single(view.Items).Class);
        Assert.Contains(view.Diagnostics, d => d.Contains("Coordinate precision is insufficient", StringComparison.Ordinal));
    });
}
