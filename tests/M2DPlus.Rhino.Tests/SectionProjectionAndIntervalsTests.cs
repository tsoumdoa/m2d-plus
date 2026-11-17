using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SectionProjectionAndIntervalsTests(RhinoHost host)
{
    [RhinoTheory]
    [InlineData(0)]
    [InlineData(65000)]
    public void PositivePolyCurveIntervalsKeepCurvatureAndSegmentDomains(double offset) => host.Run(() =>
    {
        using var curve = new PolyCurve();
        using var first = new ArcCurve(new Arc(new Plane(new Point3d(offset, 0, 0), Vector3d.ZAxis), 108.15, Math.PI / 2));
        // Touching curved/straight segments with distinct native domains.
        using var line = new LineCurve(first.PointAtEnd, first.PointAtEnd + Vector3d.YAxis * 100);
        Assert.True(curve.AppendSegment(first)); Assert.True(curve.AppendSegment(line));
        curve.Domain = new(85.25407255970795, 430.232531719681);
        double join = curve.SegmentDomain(0).Max;
        foreach (var interval in new[] { new Interval(85.33398610256067, 85.33398928779941),
            new Interval(join - 1.6e-6, join + 1.6e-6) })
        {
            using var part = CurveIntervals.Trim(curve, interval);
            Assert.NotNull(part); Assert.Equal(interval, part.Domain); Assert.True(part.GetLength() > 0);
            for (int i = 0; i <= 8; i++)
            {
                double t = interval.ParameterAt(i / 8d);
                Assert.InRange(part.PointAt(t).DistanceTo(curve.PointAt(t)), 0, 1e-8);
            }
        }
    });

    [RhinoFact]
    public void SeparatedBodiesBypassSectionsButContactsAndFarCrossingsDoNot() => host.Run(() =>
    {
        using var body = new Sphere(Point3d.Origin, 1).ToBrep();
        var near = new Plane(new Point3d(0, 0, 3), Vector3d.ZAxis);
        Assert.Equal(SectionProjectionRelation.Projection, SectionProjectionBounds.Classify(body, near, null, .001));
        Assert.Equal(SectionProjectionRelation.Excluded, SectionProjectionBounds.Classify(body, new Plane(new Point3d(0, 0, -3), Vector3d.ZAxis), null, .001));
        Assert.Equal(SectionProjectionRelation.Section, SectionProjectionBounds.Classify(body, new Plane(new Point3d(0, 0, 1), Vector3d.ZAxis), null, .001));
        Assert.Equal(SectionProjectionRelation.Section, SectionProjectionBounds.Classify(body, near, Plane.WorldXY, .001));
    });

}
