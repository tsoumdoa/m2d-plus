using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class CompositionTests(RhinoHost host)
{
    private static ViewDrawing Route(GeometryBase[] geometry, CancellationToken token = default)
    {
        var result = new ViewDrawing { Frame = new Rectangle3d(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10)), Tolerance = 1e-6 };
        var input = geometry.Select((g, i) => new VisibilityInput(g, new PreparedLeaf(i.ToString(), [i], g))).ToArray();
        string route = ComposedVisibility.Solve(input, result, ViewRequest.Parse("Top"), new(), token);
        result.Report = new(route, result.Items.Count(p => p.Class == LineClass.Unresolved), result.RoutingInvalid, new Dictionary<string, int>(), [], result.RoutingQuarantine);
        return result;
    }
    private static double Length(ViewDrawing view, string source, LineClass kind) => view.Items.Where(p => p.SourceIdentity == source && p.Class == kind)
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });

    [RhinoFact] public void EntirelyPlanarSceneUsesNoNativeIncludingPointsAndContacts() => host.Run(() =>
    {
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-1, -1, 0), new Point3d(1, 1, 1)));
        using var line = new LineCurve(new Point3d(-3, 0, -1), new Point3d(3, 0, -1));
        using var point = new global::Rhino.Geometry.Point(new Point3d(0, 0, -2));
        long calls = NativeDrawing.HldCalls;
        using var result = Route([wall, line, point]);
        Assert.Equal(calls, NativeDrawing.HldCalls); Assert.Equal(0, result.NativeCalls);
        Assert.Equal(4, Length(result, "1", LineClass.Visible), 5); Assert.Equal(2, Length(result, "1", LineClass.Hidden), 5);
        Assert.Equal(1, result.HiddenPoints); Assert.Equal(0, result.Report.UncertainCount);
    });

    [RhinoFact] public void BothOccluderFamiliesHideDisjointPartsOfOnePlanarCandidate() => host.Run(() =>
    {
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-3, -1, 0), new Point3d(-1, 1, 1)));
        using var sphere = new Sphere(new Point3d(2, 0, 0), 1).ToBrep();
        using var line = new LineCurve(new Point3d(-5, 0, -2), new Point3d(5, 0, -2));
        using var result = Route([wall, sphere, line]);
        Assert.Equal(1, result.NativeCalls); Assert.Equal(6, Length(result, "2", LineClass.Visible), 4);
        Assert.Equal(4, Length(result, "2", LineClass.Hidden), 4); Assert.Equal(0, Length(result, "2", LineClass.Unresolved), 6);
    });

    [RhinoFact] public void CurvedCandidateUsesAllPlaneRootsThroughAPlanarOccluder() => host.Run(() =>
    {
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-.5, -2, 0), new Point3d(.5, 2, 1)));
        using var circle = new Circle(new Plane(new Point3d(0, 0, -1), Vector3d.ZAxis), 1).ToNurbsCurve();
        using var result = Route([wall, circle]);
        Assert.Equal(1, result.NativeCalls);
        Assert.Equal(2 * Math.PI / 3, Length(result, "1", LineClass.Hidden), 4);
        Assert.Equal(4 * Math.PI / 3, Length(result, "1", LineClass.Visible), 4);
        Assert.Equal(0, Length(result, "1", LineClass.Unresolved), 5);
    });

    [RhinoFact] public void DisjointPlanarCandidatesNeverEnterNativeAndManagedCancellationThrows() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin, 1).ToBrep();
        using var line = new LineCurve(new Point3d(4, 0, 0), new Point3d(6, 0, 0));
        using var result = Route([sphere, line]); Assert.Equal(2, Length(result, "1", LineClass.Visible), 6);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Route([line], cancel.Token));
    });

    [RhinoFact] public void ABehindContactAliasCannotOwnTheNearerCandidatesVisibility() => host.Run(() =>
    {
        const double tolerance = 1e-4;
        using var candidate = new LineCurve(new Point3d(0,0,tolerance*.5), new Point3d(1,0,tolerance*.5));
        using var behind = new LineCurve(Point3d.Origin, new Point3d(1,0,0));
        // A planar occluder at 1.25*tolerance hides the behind alias, while the
        // nearer candidate is within depth tolerance and remains visible.
        Assert.False(ComposedVisibility.FrontWitness(candidate, behind, Plane.WorldXY, tolerance));
        Assert.True(ComposedVisibility.FrontWitness(behind, candidate, Plane.WorldXY, tolerance));
    });

    [RhinoFact] public void CompletePlanarHiddenCoverageNeedsNoForeignCandidateQuery() => host.Run(() =>
    {
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-2,-2,0),new Point3d(2,2,1)));
        using var sphere = new Sphere(new Point3d(0,0,-2),.5).ToBrep();
        using var line = new LineCurve(new Point3d(-.5,0,-4),new Point3d(.5,0,-4));
        using var result = Route([wall,sphere,line]);
        Assert.Equal(1, result.NativeCalls);
        Assert.Contains("planar edges submitted=0", result.Report.Route);
        Assert.Equal(1, Length(result,"2",LineClass.Hidden),6);
        Assert.Equal(0, Length(result,"2",LineClass.Unresolved),6);
    });

    [RhinoFact] public void NativeRegistrationsKeepM1bNumericPathsAndStableGeneratedPieceOrder() => host.Run(() =>
    {
        using var a = new LineCurve(Point3d.Origin,new Point3d(1,0,0));
        using var b = new LineCurve(new Point3d(0,1,0),new Point3d(1,1,0));
        var low = new PreparedLeaf("source",[2],a); var high = new PreparedLeaf("later",[10],a);
        VisibilityInput[] input=[new(a,high),new(b,low),new(a,low)];
        var first=VisibilityInputOrder.Canonical(input,i=>i);
        var reversed=VisibilityInputOrder.Canonical(input.AsEnumerable().Reverse(),i=>i);
        Assert.Equal([2,2,10],first.Select(i=>i.Source.Path[0]));
        Assert.Equal(first.Select(i=>GeometryFingerprint.Compute(i.Geometry)),reversed.Select(i=>GeometryFingerprint.Compute(i.Geometry)));
    });
}

