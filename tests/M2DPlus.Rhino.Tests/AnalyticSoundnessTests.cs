using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class AnalyticSoundnessTests(RhinoHost host)
{
    private static Curve Flat(Curve curve)
    {
        var flat = curve.DuplicateCurve(); Assert.True(flat.Transform(Transform.PlanarProjection(Plane.WorldXY))); return flat;
    }

    [RhinoFact] public void WiggleTouchingOnlyTheOldThreeSamplesIsNotCovered() => host.Run(() =>
    {
        // The former quarter/middle/three-quarter test passed despite the long excursions between those points.
        using var u = new PolylineCurve(new Point3d[] { new(0, 0, 0), new(.125, .1, 0), new(.25, 0, 0),
            new(.375, -.1, 0), new(.5, 0, 0), new(.625, .1, 0), new(.75, 0, 0), new(.875, -.1, 0), new(1, 0, 0) });
        u.Domain = new(0, 1);
        using var v = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)); using var uf = Flat(u); using var vf = Flat(v);
        foreach (double f in new[] { .25, .5, .75 }) Assert.True(vf.ClosestPoint(uf.PointAt(f), out _, .001));
        foreach (bool depth in new[] { false, true })
            Assert.False(BoundedCurveCover.Covers(u, uf, u.Domain, [new(v, vf, 0)], Plane.WorldXY, .002, 0, depth));
    });

    [RhinoFact] public void CoverMaySwitchPiecesAtAFoldAndDepthStillHasToHoldEverywhere() => host.Run(() =>
    {
        using var u = new PolylineCurve(new Point3d[] { new(0, 0, -1), new(1, 0, -1), new(0, 0, -1) });
        using var a = new LineCurve(new Point3d(0, 0, 0), new Point3d(.5, 0, 0));
        using var b = new LineCurve(new Point3d(.5, 0, 0), new Point3d(1, 0, 0));
        using var uf = Flat(u); using var af = Flat(a); using var bf = Flat(b);
        Assert.True(BoundedCurveCover.Covers(u, uf, u.Domain, [new(a, af, 0), new(b, bf, 0)], Plane.WorldXY, .01, 0, true));
        using var raised = new PolylineCurve(new Point3d[] { new(0, 0, -1), new(.125, 0, 1), new(.25, 0, -1),
            new(.5, 0, -1), new(.75, 0, -1), new(1, 0, -1) });
        using var rf = Flat(raised);
        Assert.False(BoundedCurveCover.Covers(raised, rf, raised.Domain, [new(a, af, 0), new(b, bf, 0)], Plane.WorldXY, .01, 0, true));
    });

    [RhinoFact] public void CoverCapCannotDiscardAnUnresolvedStretch() => host.Run(() =>
    {
        using var u = new PolylineCurve(new Point3d[] { new(0, 0, -1), new(1, 0, -1), new(0, 0, -1) });
        using var v = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)); using var uf = Flat(u); using var vf = Flat(v);
        Assert.False(BoundedCurveCover.Covers(u, uf, u.Domain, [new(v, vf, 0)], Plane.WorldXY, .001, 0, true, cap: 1));
    });

    private static CutTubeRecognition.Surface Cylinder(Point3d o, Vector3d d, double slack = 0) => new(true, o, d, 1, CsgRole.Free, slack, false, 1);

    [RhinoFact] public void CoincidentCirclesWithDifferentSeamsHaveAFastCoverCertificate() => host.Run(() =>
    {
        using var u = new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 100).ToNurbsCurve();
        using var v = new Circle(Plane.WorldXY, 100).ToNurbsCurve(); Assert.True(v.ChangeClosedCurveSeam(.371));
        using var uf = Flat(u); using var vf = Flat(v); var budget = new BoundedCurveCover.WorkBudget();
        Assert.True(BoundedCurveCover.Covers(u, uf, u.Domain, [new(v, vf, 0)], Plane.WorldXY, .001, 0, true, cap: 8192, budget: budget));
        Assert.Equal(0, budget.CapHits); Assert.True(budget.Used < 12000, $"Work {budget.Used}");
        Assert.True(budget.FirstOrderProofs > 0);
    });

    [RhinoFact] public void SharedCoverBudgetBoundsTheWholePass() => host.Run(() =>
    {
        using var u = new PolylineCurve(new Point3d[] { new(0, 0, -1), new(1, 0, -1), new(0, 0, -1) });
        using var v = new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)); using var uf = Flat(u); using var vf = Flat(v);
        var budget = new BoundedCurveCover.WorkBudget(3);
        for (int i = 0; i < 10; i++) Assert.False(BoundedCurveCover.Covers(u, uf, u.Domain, [new(v, vf, 0)], Plane.WorldXY, .001, 0, true, budget: budget));
        Assert.Equal(3, budget.Used); Assert.Equal(10, budget.CapHits);
    });
    private static CutTubeRecognition.Surface PlaneSurface(Point3d o, Vector3d n, double slack = 0) => new(false, o, n, 0, CsgRole.Free, slack, false, 1);

    [RhinoFact] public void ShallowPlaneCylinderGapIncludesTheInverseCrossingSine() => host.Run(() =>
    {
        var a = Cylinder(Point3d.Origin, Vector3d.ZAxis); var b = PlaneSurface(new(Math.Cos(.02), 0, 0), Vector3d.XAxis);
        using var edge = new LineCurve(new Point3d(Math.Cos(.02), .018, 0), new Point3d(Math.Cos(.02), .018, 1));
        double da = Math.Abs(CutTubeRecognition.Distance(a, edge.PointAtStart)), db = Math.Abs(CutTubeRecognition.Distance(b, edge.PointAtStart));
        double sine = Vector3d.CrossProduct(new Vector3d(edge.PointAtStart.X, edge.PointAtStart.Y, 0) / (1 - da), b.Direction).Length;
        double gap = CutTubeRecognition.IntersectionGap(edge, a, b, da, db, .1);
        Assert.True(double.IsFinite(gap)); Assert.True(gap >= (da + db) / sine);
        Assert.True(gap > 40 * (da + db));
        Assert.True(gap >= Math.Sin(.02) - .018);
        using var tangent = new LineCurve(new Point3d(1, 0, 0), new Point3d(1, 0, 1));
        Assert.True(double.IsPositiveInfinity(CutTubeRecognition.IntersectionGap(tangent, a, b, .001, .001, .1)));
    });

    [RhinoFact] public void CylinderCylinderAngleIsBoundedBetweenSamples() => host.Run(() =>
    {
        var a = Cylinder(Point3d.Origin, Vector3d.ZAxis); var b = Cylinder(new(1.9996, 0, 0), Vector3d.YAxis);
        double x = .9998, y = Math.Sqrt(1 - x * x);
        using var edge = new LineCurve(new Point3d(x, y + 1e-6, y + 1e-6), new Point3d(x, y + 2e-6, y + 2e-6));
        double da = CutTubeRecognition.Deviation(edge, a, 1e-10, .1), db = CutTubeRecognition.Deviation(edge, b, 1e-10, .1);
        double gap = CutTubeRecognition.IntersectionGap(edge, a, b, da, db, .1);
        Assert.True(double.IsFinite(gap)); Assert.True(gap > 20 * (da + db));
        // A span passing through the tangent point away from its midpoint is refused even though its endpoints
        // and centre cross cleanly. Curvature, rather than a fixed sampling pattern, protects the interior.
        var touching = Cylinder(new(2, 0, 0), Vector3d.YAxis);
        using var pinch = new LineCurve(new Point3d(1, -.13, -.13), new Point3d(1, .27, .27));
        Assert.True(double.IsPositiveInfinity(CutTubeRecognition.IntersectionGap(pinch, a, touching, .01, .01, 1)));
    });

    [RhinoFact] public void SliverWithoutAnInteriorPointOrInsideAnotherSlackBandProvidesNoEvidence() => host.Run(() =>
    {
        var a = PlaneSurface(Point3d.Origin, Vector3d.ZAxis, .01); var b = PlaneSurface(new(0, 0, .015), Vector3d.ZAxis, .01);
        var csg = new AnalyticCsg([new(false, new(0, 0, 0), new(0, 0, 1), 0, CsgRole.Inside, .01)], new(-1, -1, -1), new(1, 1, 1));
        Assert.False(CutTubeRecognition.CheckSliver([], 0, [a], csg, .001));
        Assert.False(CutTubeRecognition.CheckSliver([(Point3d.Origin, Vector3d.ZAxis)], 0, [a, b], csg, .001));
        Assert.True(CutTubeRecognition.CheckSliver([(Point3d.Origin, Vector3d.ZAxis)], 0, [a], csg, .001));
        var oblique = PlaneSurface(new(.015, 0, 0), Vector3d.XAxis, .01);
        var independent = new AnalyticCsg([new(false, new(0, 0, 0), new(0, 0, 1), 0, CsgRole.Inside, .01),
            new(false, new(.015, 0, 0), new(1, 0, 0), 0, CsgRole.Free, .01)], new(-1, -1, -1), new(1, 1, 1), [0, 1], []);
        // .015 < .01+.01+4tol would reject this face. Both actual probes move parallel to the other plane,
        // remain .015 from it, and are outside its .01 band: the closed-form distances prove they are clear.
        Assert.True(CutTubeRecognition.CheckSliver([(Point3d.Origin, Vector3d.ZAxis)], 0, [a, oblique], independent, .001));
        // An earlier ambiguous sample cannot conceal positive evidence contradicting the orientation later.
        Assert.Equal(CutTubeRecognition.OrientationContradiction,
            CutTubeRecognition.SliverFailure([(Point3d.Origin, Vector3d.ZAxis), (new Point3d(0, 0, .5), Vector3d.ZAxis)], 0, [a, b], csg, .001));
    });

    [RhinoFact] public void ClosedCylinderGapRemainsBoundedAtATangentBranchEndpoint() => host.Run(() =>
    {
        var a = Cylinder(Point3d.Origin, Vector3d.ZAxis); var b = Cylinder(Point3d.Origin, Vector3d.YAxis);
        using var edge = new LineCurve(new Point3d(.99999, 0, .00001), new Point3d(1, 0, 0));
        double da = CutTubeRecognition.Deviation(edge, a, 1e-10, .1), db = CutTubeRecognition.Deviation(edge, b, 1e-10, .1);
        double gap = CutTubeRecognition.CylinderGap(edge, a, b, da, db, .1);
        Assert.True(double.IsFinite(gap)); Assert.InRange(gap, 0, .01);
        for (int i = 0; i <= 100; i++)
        {
            var p = edge.PointAt(edge.Domain.ParameterAt(i / 100.0));
            var q = Assert.IsType<Point3d>(CutTubeRecognition.Onto(a, b, p));
            Assert.True(p.DistanceTo(q) <= gap);
            Assert.InRange(Math.Abs(CutTubeRecognition.Distance(a, q)) + Math.Abs(CutTubeRecognition.Distance(b, q)), 0, 1e-9);
        }
    });

    [RhinoFact] public void PinchSquareRootBoundShrinksAwayFromTheEndpoint() => host.Run(() =>
    {
        var a = Cylinder(Point3d.Origin, Vector3d.ZAxis); var b = Cylinder(Point3d.Origin, Vector3d.YAxis);
        using var edge = new ArcCurve(new Arc(Plane.WorldXY, 1, Math.PI / 2)).ToNurbsCurve();
        var lift = Transform.Identity; lift.M21 = 1; lift.M22 = 0; lift.M23 = 1e-6; Assert.True(edge.Transform(lift));
        double da = CutTubeRecognition.Deviation(edge, a, 1e-8, .1), db = CutTubeRecognition.Deviation(edge, b, 1e-8, .1);
        double whole = CutTubeRecognition.CylinderGap(edge, a, b, da, db, .1, out bool root);
        Assert.True(root); Assert.True(double.IsFinite(whole));
        using var regular = edge.Trim(edge.Domain.ParameterAt(.5), edge.Domain.ParameterAt(.75));
        double local = CutTubeRecognition.CylinderGap(regular, a, b, da, db, .1, out bool localRoot);
        Assert.False(localRoot); Assert.True(local < whole / 100, $"whole {whole:R}, local {local:R}");
    });
}
