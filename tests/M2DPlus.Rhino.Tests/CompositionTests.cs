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
    private static ViewDrawing Route(GeometryBase[] geometry, CancellationToken token = default, NativeSettings? settings = null)
    {
        var result = new ViewDrawing { Frame = new Rectangle3d(Plane.WorldXY, new Interval(-10, 10), new Interval(-10, 10)), Tolerance = 1e-6 };
        var input = geometry.Select((g, i) => new VisibilityInput(g, new PreparedLeaf(i.ToString(), [i], g))).ToArray();
        string route = ComposedVisibility.Solve(input, result, ViewRequest.Parse("Top"), settings ?? new(), token);
        result.Report = new(route, result.Items.Count(p => p.Class == LineClass.Unresolved), result.RoutingInvalid, new Dictionary<string, int>(), [], result.RoutingQuarantine);
        return result;
    }
    private static double Length(ViewDrawing view, string source, LineClass kind) => view.Items.Where(p => p.SourceIdentity == source && p.Class == kind)
        .Sum(p => { using var g = p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });

    [RhinoFact] public void HiddenWitnessReductionKeepsTheFrontSourceAndBothCrossingDepthGraphs() => host.Run(() =>
    {
        var groups=new Dictionary<(double,double,double,double),List<ComposedVisibility.HiddenSupport>>();
        ComposedVisibility.RetainFrontHidden(groups,new(0,new(0,0,0),new(1,0,0)));
        ComposedVisibility.RetainFrontHidden(groups,new(1,new(1,0,1),new(0,0,1)));
        Assert.Equal(1,Assert.Single(Assert.Single(groups).Value).Edge);
        ComposedVisibility.RetainFrontHidden(groups,new(2,new(0,0,0),new(1,0,2)));
        Assert.Equal(new[]{1,2},Assert.Single(groups).Value.Select(s=>s.Edge));
        ComposedVisibility.RetainFrontHidden(groups,new(3,new(0,0,-1),new(1,0,0)));
        Assert.Equal(2,Assert.Single(groups).Value.Count);
    });

    [RhinoTheory]
    [InlineData("points_cut_solid")]
    [InlineData("block_expanded")]
    [InlineData("region_rhs")]
    public void PlanarSectionDoesNotPublishNumericalPointResidues(string fixture) => host.Run(() =>
    {
        using var document = RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory,"fixtures","sections",fixture + ".3dm"));
        using var cache = new SceneCache(); using var lease = BaselineTests.Capture(document,cache);
        var view = ViewRequest.Parse(new Plane(lease.Value.Bounds.Center,Vector3d.XAxis,Vector3d.ZAxis));
        using var result = NativeDrawing.SolveBorrowedAsync(lease.Value,new(lease.Value.Revision,view.Revision,"projection-regression",1),[view],new(),default,null).GetAwaiter().GetResult();
        Assert.Null(result.Views[0].Error); Assert.Equal(0,result.Views[0].NativeCalls);
        Assert.All(result.Views[0].Items,p => { using var geometry = p.CopyGeometry(); if (geometry is Curve c) Assert.True(c.GetLength() > 1e-12); });
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroKernelScenesUseTheSameNativeAdapterIncludingClippedCameraBounds(bool cut) => host.Run(() =>
    {
        using var document = RhinoDoc.CreateHeadless(null); document.ModelAbsoluteTolerance=1e-6;
        using var main = new Sphere(new Point3d(0,0,-1),2).ToBrep();
        using var removed = new Sphere(new Point3d(0,0,40),1).ToBrep();
        document.Objects.AddBrep(main); document.Objects.AddBrep(removed);
        using var cache = new SceneCache(); using var lease = BaselineTests.Capture(document,cache);
        var view = cut ? ViewRequest.Parse(Plane.WorldXY) : ViewRequest.Parse("SW");
        var identity = new RequestIdentity(lease.Value.Revision,view.Revision,"native-fallback",1);
        // Spheres are exact analytic solids now; this test is about the native fallback, so the analytic tier is off.
        using var routed = NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[view],new() { Analytic = false },default,null).GetAwaiter().GetResult();
        using var native = NativeDrawing.SolveBorrowedAsync(lease.Value,identity,[view],new(NativeOnly:true),default,null).GetAwaiter().GetResult();
        var a=routed.Views[0]; var b=native.Views[0];
        Assert.Null(a.Error); Assert.Null(b.Error); Assert.Equal(0,a.KernelEdges); Assert.Equal(1,a.NativeCalls);
        Assert.False(a.Phases.Seconds.ContainsKey("planar extraction"));
        foreach (var kind in new[] {LineClass.Visible,LineClass.Hidden,LineClass.Unresolved})
        {
            double Length(ViewDrawing drawing) => drawing.Items.Where(p => p.Class==kind).Sum(p => { using var g=p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0; });
            Assert.Equal(Length(b),Length(a),6);
        }
        Assert.Equal(b.Regions.Select(r => r.Region.Area),a.Regions.Select(r => r.Region.Area));
    });

    [RhinoFact] public void NativeMeshPolicyDoesNotInventATriangulationForANonplanarQuadOrNonmanifoldEdge() => host.Run(() =>
    {
        using var quad = new Mesh();
        quad.Vertices.Add(0,0,0); quad.Vertices.Add(1,0,0); quad.Vertices.Add(1,1,0); quad.Vertices.Add(0,1,1);
        quad.Faces.AddFace(0,1,2,3);
        using var nonmanifold = new Mesh();
        nonmanifold.Vertices.Add(0,0,0); nonmanifold.Vertices.Add(1,0,0); nonmanifold.Vertices.Add(0,1,0); nonmanifold.Vertices.Add(0,-1,0); nonmanifold.Vertices.Add(0,0,1);
        nonmanifold.Faces.AddFace(0,1,2); nonmanifold.Faces.AddFace(1,0,3); nonmanifold.Faces.AddFace(0,1,4);
        foreach (var mesh in new[] {quad,nonmanifold})
        {
            var extraction = new PlanarExtraction();
            Assert.False(extraction.Add(mesh,Transform.Identity,Plane.WorldXY,1e-6,"mesh",nativeMeshCandidates:true));
            Assert.Empty(extraction.Faces); Assert.Empty(extraction.Edges);
        }
    });

    [RhinoFact] public void NativeSkewRoundoffOnCollinearNurbsLinesHasCertifiedOverlapWithoutNativeCci() => host.Run(() =>
    {
        using var shortSupport = new LineCurve(new Point3d(5.499999999999986,17.999999999999957,18.00000000000003),new Point3d(5.549999999999983,17.999999999999986,18.00000000000003));
        using var shortLine = shortSupport.ToNurbsCurve();
        using var longSupport = new LineCurve(new Point3d(5.999999999999986,17.999999999999986,18.000000000000014),new Point3d(-1.4210854715202004e-14,17.999999999999986,18.00000000000003));
        using var longLine = longSupport.ToNurbsCurve();
        var plane = new Plane(Point3d.Origin,new Vector3d(1,1,1));
        Assert.True(ComposedVisibility.TryLinearOverlap(shortLine,longLine,plane,1e-4,out var a,out var b));
        Assert.Equal(shortLine.Domain.Min,a.Min,10); Assert.Equal(shortLine.Domain.Max,a.Max,10);
        using var ca=CurveIntervals.Trim(shortLine,a); using var cb=CurveIntervals.Trim(longLine,b);
        Assert.True(ComposedVisibility.FrontWitness(ca!,cb!,plane,1e-4));
    });

    [RhinoFact] public void TolerantWorldOverlapCannotUseABehindHiddenAliasAsTheDuplicateOwner() => host.Run(() =>
    {
        const double tol = 1e-4;
        using var candidate = new LineCurve(Point3d.Origin,new Point3d(1,0,0));
        using var behind = new LineCurve(new Point3d(0,0,-tol*.5),new Point3d(1,0,-tol*.5));
        var tag = new ComposedVisibility.Tag(new(candidate,new("candidate",[0],candidate)),0);
        var duplicate = new ComposedVisibility.NativeRecord(candidate,tag,LineClass.Unresolved,false,true);
        var retained = new ComposedVisibility.NativeRecord(behind,new(new(behind,new("behind",[1],behind)),1),LineClass.Hidden,false,false);
        var owned = new List<GeometryBase>();
        try
        {
            var parts = ComposedVisibility.ResolveDuplicates([duplicate],Plane.WorldXY,tol,owned,default,[retained]).ToArray();
            Assert.All(parts,p => Assert.Equal(LineClass.Unresolved,p.Class));
            Assert.Equal(1,parts.Sum(p => p.World.GetLength()),6);
        }
        finally { foreach (var geometry in owned) geometry.Dispose(); }
    });

    [RhinoFact] public void RetainedKernelHiddenOwnerCertifiesOnlyItsActualNativeDuplicateOverlap() => host.Run(() =>
    {
        using var candidate = new LineCurve(new Point3d(-1,0,0),new Point3d(1,0,0));
        using var witness = new LineCurve(new Point3d(0,0,1),new Point3d(1,0,1));
        var source = new PreparedLeaf("candidate",[0],candidate);
        var tag = new ComposedVisibility.Tag(new(candidate,source),0);
        var duplicate = new ComposedVisibility.NativeRecord(candidate,tag,LineClass.Unresolved,false,true);
        var retained = new ComposedVisibility.NativeRecord(witness,new(new(witness,new("kernel",[1],witness)),1),LineClass.Hidden,false,false);
        var owned = new List<GeometryBase>();
        try
        {
            var parts = ComposedVisibility.ResolveDuplicates([duplicate],Plane.WorldXY,1e-6,owned,default,[retained]).ToArray();
            Assert.Equal(1,parts.Where(p => p.Class == LineClass.Hidden).Sum(p => p.World.GetLength()),6);
            Assert.Equal(1,parts.Where(p => p.Class == LineClass.Unresolved).Sum(p => p.World.GetLength()),6);
        }
        finally { foreach (var geometry in owned) geometry.Dispose(); }
    });

    [RhinoFact] public void IdenticalAndReversedKernelCandidatesShareOneForeignVisibilityQuery() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var a = new LineCurve(new Point3d(-3,0,-2),new Point3d(3,0,-2));
        using var b = new LineCurve(new Point3d(3,0,-2),new Point3d(-3,0,-2));
        int queries = 0;
        // The sphere stands for any native body: the analytic tier is off so that the foreign query exists.
        using var result = Route([sphere,a,b],settings:new() { Analytic = false, ObserveNativeInputs = inputs => queries=inputs.Count(i => i.KernelCandidate) });
        Assert.Equal(1,queries);
        Assert.DoesNotContain(result.Items,p => p.Class == LineClass.Unresolved);
        Assert.Equal(4,Length(result,"1",LineClass.Visible)+Length(result,"2",LineClass.Visible),5);
        Assert.Equal(2,Length(result,"1",LineClass.Hidden)+Length(result,"2",LineClass.Hidden),5);
    });

    [RhinoFact] public void LinearDuplicateWitnessDoesNotRequireMatchingSplineParameterization() => host.Run(() =>
    {
        using var candidate = new LineCurve(new Point3d(6,17.14,18),new Point3d(6,17.05,18));
        using var witness = Curve.CreateInterpolatedCurve(new[] {new Point3d(6,17.14,18),new Point3d(6,17.11,18),new Point3d(6,17.08,18),new Point3d(6,17.05,18)},3);
        var normal = new Vector3d(1,1,1); normal.Unitize(); var plane = new Plane(Point3d.Origin,normal);
        Assert.True(ComposedVisibility.FrontWitness(candidate,witness,plane,1e-4));
        witness.Translate(-normal*5e-5);
        Assert.False(ComposedVisibility.FrontWitness(candidate,witness,plane,1e-4));
    });

    [RhinoFact] public void NativeInverseFrameRoundoffDoesNotLoseACoincidentDuplicateWitness() => host.Run(() =>
    {
        using var candidate = new LineCurve(new Point3d(6,17.14,18),new Point3d(6,17.05,18));
        using var witness = new LineCurve(new Point3d(6,17.14,Math.BitDecrement(18d)),new Point3d(6,17.05,Math.BitDecrement(18d)));
        var normal = new Vector3d(1,1,1); normal.Unitize(); var plane = new Plane(Point3d.Origin,normal);
        Assert.True(ComposedVisibility.FrontWitness(candidate,witness,plane,1e-4));
    });

    [RhinoFact] public void PlanarOcclusionDecidesEvenAnAmbiguousCurvedDuplicate() => host.Run(() =>
    {
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-3,-3,1),new Point3d(3,3,2)));
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var alias = sphere.DuplicateBrep();
        using var result = Route([wall,sphere,alias]);
        Assert.DoesNotContain(result.Items,p => p.Class == LineClass.Unresolved);
        Assert.Equal(0,Length(result,"1",LineClass.Visible)); Assert.Equal(0,Length(result,"2",LineClass.Visible));
    });

    [RhinoFact] public void WeldedNonplanarMeshRetainsNativeSmoothCandidateLoci() => host.Run(() =>
    {
        using var sphere = Mesh.CreateFromSphere(new Sphere(Point3d.Origin,2),12,8);
        sphere.Weld(Math.PI);
        var extraction = new PlanarExtraction();
        Assert.False(extraction.Add(sphere,Transform.Identity,Plane.WorldXY,1e-6,"mesh",nativeMeshCandidates:true));
        Assert.Empty(extraction.Edges); Assert.Empty(extraction.Faces);
        using var result = Route([sphere]);
        Assert.Equal(1,result.NativeCalls); Assert.Equal(0,result.KernelEdges);
        Assert.DoesNotContain(result.Items,p => p.Class == LineClass.Unresolved);
    });

    [RhinoFact] public void ProvenanceOverlapAtDomainEndCannotCreateARoundoffSliver() => host.Run(() =>
    {
        using var edge = new LineCurve(new Point3d(-1,0,0),new Point3d(1,0,0));
        using var cut = new LineCurve(new Point3d(-1,0,0),new Point3d(Math.BitDecrement(1d),0,0));
        var parts = M2DPlus.Rhino.Sections.EdgeProvenance.Split(edge,[(cut,M2DPlus.Core.Sections.EdgeOrigin.PhysicalCut)],1e-4);
        try { Assert.Single(parts); Assert.Equal(M2DPlus.Core.Sections.EdgeOrigin.PhysicalCut,parts[0].Origin); }
        finally { foreach (var part in parts) part.Dispose(); }
    });

    [RhinoFact] public void RoundoffProjectionOfARealEdgeIsDroppedButIsolatedShortLinesSurvive() => host.Run(() =>
    {
        using var edge = new LineCurve(new Point3d(1,0,-1),new Point3d(1,0,1));
        using var residue = new LineCurve(new Point3d(1,0,1),new Point3d(Math.BitIncrement(1d),0,1));
        Assert.True(ComposedVisibility.ProjectionResidue(edge,residue,1e-4));
        using var shortEdge = new LineCurve(new Point3d(1,0,0),new Point3d(1 + 1e-8,0,0));
        Assert.False(ComposedVisibility.ProjectionResidue(shortEdge,shortEdge,1e-4));
    });

    [RhinoFact] public void PlanarBoxProfilesAndInteriorCreasesAreDistinguished() => host.Run(() =>
    {
        using var box = Brep.CreateFromBox(new BoundingBox(new Point3d(-1,-1,-1),new Point3d(1,1,1)));
        var extraction = new PlanarExtraction();
        Assert.True(extraction.Add(box,Transform.Identity,Plane.WorldXY,1e-6,"box"));
        Assert.Equal(4,extraction.Profiles.Count(p => p));
        using var result = Route([box]);
        Assert.All(result.Items.Where(p => p.Class == LineClass.Visible),p => Assert.True(p.Profile));
    });

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
        using var result = Route([wall, sphere, line], settings: new() { Analytic = false });   // planar + native
        Assert.Equal(1, result.NativeCalls); Assert.Equal(6, Length(result, "2", LineClass.Visible), 4);
        Assert.Equal(4, Length(result, "2", LineClass.Hidden), 4); Assert.Equal(0, Length(result, "2", LineClass.Unresolved), 6);
        using var exact = Route([wall, sphere, line]);                                         // planar + exact analytic: the same drawing, no HLD
        Assert.Equal(0, exact.NativeCalls); Assert.Equal(6, Length(exact, "2", LineClass.Visible), 6);
        Assert.Equal(4, Length(exact, "2", LineClass.Hidden), 6); Assert.Equal(0, Length(exact, "2", LineClass.Unresolved), 6);
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
        using var result = Route([wall,sphere,line], settings: new() { Analytic = false });   // the sphere stands for any native body
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

