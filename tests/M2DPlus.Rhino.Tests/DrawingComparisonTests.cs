using M2DPlus.Core;
using M2DPlus.Measurements;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;
using Point = Rhino.Geometry.Point;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class DrawingComparisonTests(RhinoHost host)
{
    [Fact] public void BaselineRejectsEitherRegressionAndNonFiniteMeasurements()
    {
        var band = new RegressionBand(10, 1, 20, 2, .5, .05, .25, .025);
        Assert.True(band.Accepts(10.5, 1.05, 20.25, 2.025));
        Assert.True(band.Accepts(0, 0, 0, 0));
        Assert.False(band.Accepts(10.501, 1, 20, 2));
        Assert.False(band.Accepts(10, 1.051, 20, 2));
        Assert.False(band.Accepts(10, 1, 20.251, 2));
        Assert.False(band.Accepts(10, 1, 20, 2.026));
        Assert.False(band.Accepts(0, 0, double.NaN, 0));
        Assert.False(band.Accepts(0, 0, 0, double.PositiveInfinity));
        Assert.False(band.Accepts(double.NaN, 0, 0, 0));
        Assert.False(band.Accepts(0, double.PositiveInfinity, 0, 0));
    }
    private static void Stroke(ViewDrawing drawing, LineClass kind, double y = 0)
    {
        var curve = new LineCurve(new Point3d(0, y, 0), new Point3d(10, y, 0));
        drawing.Pieces.Add(new(curve, new("control", [], curve), kind));
    }
    [Fact] public void CalibrationPoolsErrorObservationsWithoutUsingUnrelatedClassNoise()
    {
        static DrawingComparison.Direction Direction(double length, double wrong) => new(
            [new("control", LineClass.Visible, length, 100, length - wrong, wrong, 0, 0),
             new("control", LineClass.Hidden, 10000, 100, 0, 10000, 0, 0)], new(0, 0, 0), 0);
        var samples = new[] { new DrawingComparison.Result(Direction(1000, 10), Direction(500, 20)),
            new DrawingComparison.Result(Direction(1000, 40), Direction(500, 60)) };
        var band = RegressionBand.Observe(samples, .5);
        Assert.Equal(40, band.WrongVisibleMm); Assert.Equal(60, band.WrongHiddenMm);
        Assert.Equal(30.5, band.ToleranceMm); Assert.Equal(40.5, band.HiddenToleranceMm);
        Assert.True(band.Accepts(70.5, 7.05, 100.5, 20.1));
        Assert.False(band.Accepts(70.501, 7.05, 100.5, 20.1));
        Assert.False(band.Accepts(70.5, 7.05, 100.501, 20.1));
        Assert.Throws<InvalidDataException>(() => RegressionBand.Observe([], .5));
        Assert.Throws<InvalidDataException>(() => RegressionBand.Observe(
            [samples[0], new(Direction(1000, double.NaN), Direction(500, 60))], .5));
    }
    private static void Dot(ViewDrawing drawing, LineClass kind)
    {
        var point = new Point(new Point3d(5, 5, 0));
        drawing.Pieces.Add(new(point, new("point", [], point), kind));
    }
    [RhinoFact] public void SelfComparisonHasNoDisagreement() => host.Run(() =>
    {
        using var drawing = new ViewDrawing(); Stroke(drawing, LineClass.Visible); Stroke(drawing, LineClass.Hidden, 2);
        var circle = new Circle(new Point3d(20, 20, 0), 3).ToNurbsCurve();
        drawing.Pieces.Add(new(circle, new("curved-control", [], circle), LineClass.Visible));
        Dot(drawing, LineClass.Visible);
        var result = DrawingComparison.Compare(drawing, drawing);
        Assert.Equal(0, result.WrongHiddenMm); Assert.Equal(0, result.WrongHiddenPercent);
        foreach (var direction in new[] { result.PipesToHld, result.HldToPipes })
        {
            Assert.All(direction.Classes, c => { Assert.Equal(0, c.OppositeOnly); Assert.Equal(0, c.UnresolvedOnly); Assert.Equal(0, c.Absent); });
            Assert.Equal(0, direction.UnresolvedMm); Assert.Equal(0, direction.Points.ClassDisagreements); Assert.Equal(0, direction.Points.Absent);
        }
    });
    [RhinoFact] public void TenMillimetreSwapIsSeparateFromMissingGeometry() => host.Run(() =>
    {
        using var pipes = new ViewDrawing(); using var hld = new ViewDrawing();
        Stroke(pipes, LineClass.Visible); Stroke(hld, LineClass.Hidden);
        var swap = DrawingComparison.Compare(pipes, hld);
        Assert.InRange(swap.WrongVisibleMm, 9.75, 10.25); Assert.Equal(100, swap.WrongVisiblePercent, 8);
        Assert.Equal(0, swap.PipesToHld.MissingMm); Assert.Equal(0, swap.WrongHiddenMm);
        var reverse = DrawingComparison.Compare(hld, pipes);
        Assert.InRange(reverse.WrongHiddenMm, 9.75, 10.25); Assert.Equal(100, reverse.WrongHiddenPercent, 8);
        Assert.InRange(DrawingComparison.WrongHiddenSpans(hld, pipes).Sum(s => s.Mm), 9.75, 10.25); Assert.Equal(0, reverse.WrongVisibleMm);
        using var empty = new ViewDrawing();
        var deleted = DrawingComparison.Compare(pipes, empty);
        Assert.InRange(deleted.PipesToHld.MissingMm, 9.75, 10.25);
        Assert.Equal(0, deleted.WrongVisibleMm); Assert.Equal(0, deleted.WrongHiddenMm);
        Assert.InRange(DrawingComparison.Compare(empty, pipes).HldToPipes.MissingMm, 9.75, 10.25);
    });
    [RhinoFact] public void CoincidentSameClassWinsAndUnresolvedAndPointsStaySeparate() => host.Run(() =>
    {
        using var pipes = new ViewDrawing(); using var hld = new ViewDrawing();
        Stroke(pipes, LineClass.Visible); Stroke(hld, LineClass.Hidden); Stroke(hld, LineClass.Visible);
        Dot(pipes, LineClass.Visible); Dot(hld, LineClass.Hidden);
        Assert.Equal(0, DrawingComparison.Compare(pipes, hld).WrongVisibleMm);
        Assert.Equal(1, DrawingComparison.Compare(pipes, hld).PipesToHld.Points.ClassDisagreements);
        using var orange = new ViewDrawing(); Stroke(orange, LineClass.Unresolved);
        var result = DrawingComparison.Compare(pipes, orange);
        Assert.Equal(0, result.WrongVisibleMm); Assert.Equal(10, result.HldToPipes.UnresolvedMm, 8);
        Assert.Equal(10, result.PipesToHld[LineClass.Visible].UnresolvedOnly, 8);
        Assert.Equal(1, result.PipesToHld.Points.Absent);
    });
    [RhinoFact] public void SelfOverlapExcludesItselfAndCountsBothCopiesOnlyWithinClass() => host.Run(() =>
    {
        using var drawing = new ViewDrawing(); Stroke(drawing, LineClass.Visible);
        Assert.Equal(0, DrawingComparison.SelfOverlap(drawing).Sum(o => o.Mm));
        Stroke(drawing, LineClass.Hidden);
        Assert.Equal(0, DrawingComparison.SelfOverlap(drawing).Sum(o => o.Mm));
        Stroke(drawing, LineClass.Visible);
        Assert.Equal(20, DrawingComparison.SelfOverlap(drawing).Single(o => o.Class == LineClass.Visible).Mm, 8);
        Assert.Equal(0, DrawingComparison.SelfOverlap(drawing).Single(o => o.Class == LineClass.Hidden).Mm);
    });
    [RhinoFact] public void SelfOverlapExcludesEveryIndexedSpanOfOneLongStroke() => host.Run(() =>
    {
        using var drawing = new ViewDrawing();
        var curve = new LineCurve(Point3d.Origin, new Point3d(200, 0, 0));
        drawing.Pieces.Add(new(curve, new("long-control", [], curve), LineClass.Visible));
        Assert.Equal(0, DrawingComparison.SelfOverlap(drawing).Sum(o => o.Mm));
    });
    [RhinoFact] public void ZeroLengthProjectionContributesNoInventedLength() => host.Run(() =>
    {
        using var drawing = new ViewDrawing(); using var empty = new ViewDrawing();
        var curve = new LineCurve(Point3d.Origin, Point3d.Origin);
        drawing.Pieces.Add(new(curve, new("zero", [], curve), LineClass.Visible));
        var result = DrawingComparison.Compare(drawing, empty);
        Assert.Equal(1, result.PipesToHld.ZeroLengthCurves);
        Assert.Equal(0, result.PipesToHld.MissingMm); Assert.Equal(0, result.WrongVisibleMm);
    });
    [RhinoFact] public void ExactSpanIndexMatchesFullCircleDistanceAtEveryStation() => host.Run(() =>
    {
        using var pipes = new ViewDrawing(); using var hld = new ViewDrawing();
        var source = new Circle(Point3d.Origin, 10).ToNurbsCurve();
        var target = new Circle(new Point3d(.075, 0, 0), 10).ToNurbsCurve();
        pipes.Pieces.Add(new(source, new("circle-a", [], source), LineClass.Visible));
        hld.Pieces.Add(new(target, new("circle-b", [], target), LineClass.Hidden));
        var length = source.GetLength(); var count = (int)Math.Ceiling(length / .25);
        var expected = 0d;
        for (var i = 0; i < count; i++)
        {
            var point = source.PointAtNormalizedLength((i + .5) / count);
            Assert.True(target.ClosestPoint(point, out var t));
            if (target.PointAt(t).DistanceTo(point) <= .05) expected += length / count;
        }
        var measured = DrawingComparison.Compare(pipes, hld);
        Assert.Equal(expected, measured.WrongVisibleMm, 7);
        Assert.Equal(length - expected, measured.PipesToHld.MissingMm, 7);
    });
}
