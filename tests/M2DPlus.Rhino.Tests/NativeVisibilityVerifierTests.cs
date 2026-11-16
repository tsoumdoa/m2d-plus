using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class NativeVisibilityVerifierTests(RhinoHost host)
{
    private static ViewDrawing Output(Plane? plane = null) => new()
    {
        Frame = new Rectangle3d(plane ?? Plane.WorldXY,new Interval(-5,5),new Interval(-5,5)), Tolerance = 1e-6
    };
    private static VisibilityInput Input(GeometryBase g, int i = 0) => new(g,new(i.ToString(),[i],g));

    [RhinoFact] public void EarlyOffsetRejectionPreservesDecisionsWitnessesAndOriginalBudgetCoverage() => host.Run(() =>
    {
        using var body=new BoundingBox(new Point3d(-1,-1,-1),new Point3d(1,1,1)).ToBrep();
        using var a=Output();using var b=Output();
        using var exhaustive=new NativeVisibilityVerifier([Input(body)],a,new(MaxRayTests:15){ExhaustiveOffsets=true},default,new PlanarExtraction());
        using var shortcut=new NativeVisibilityVerifier([Input(body)],b,new(MaxRayTests:15),default,new PlanarExtraction());
        foreach(var point in new[]{new Point3d(0,0,1-2e-6),new Point3d(0,0,0),new Point3d(1-2e-6,0,0),new Point3d(0,0,0)})
        {
            Assert.Equal(exhaustive.Check(point,default,out var expected),shortcut.Check(point,default,out var actual));
            Assert.Equal(expected,actual);
        }
        Assert.True(shortcut.NativeCasts<exhaustive.NativeCasts);
        exhaustive.Report();shortcut.Report();
        // Coverage and all logical work caps remain identical, including the
        // fourth sample that is rejected by the original 15-ray budget.
        Assert.Equal(a.Diagnostics.Single().Split("; native casts=")[0],b.Diagnostics.Single().Split("; native casts=")[0]);
    });

    [RhinoTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedAuditEarlyExitKeepsCompleteDrawingBytesAndUnsampledWarnings(bool freeform) => host.Run(() =>
    {
        var points=Enumerable.Range(0,4).SelectMany(i=>Enumerable.Range(0,4).Select(j=>
        {
            double x=-1+2*i/3d,y=-1+2*j/3d;
            return new Point3d(x,y,.3*x*y+.1*x*x*y*y);
        }));
        using var surface=NurbsSurface.CreateFromPoints(points,4,4,3,3);
        using var body=freeform?surface.ToBrep():new Sphere(Point3d.Origin,1).ToBrep();
        using var curve=new LineCurve(new Point3d(-1.1,0,-2),new Point3d(1.1,0,-2));
        using var initial=Output();
        initial.Pieces.Add(new(curve.DuplicateCurve(),Input(curve,1).Source,LineClass.Visible,curve.DuplicateCurve()));
        var bytes=WorkerCodec.CaptureDrawing(initial);
        using var exhaustive=WorkerCodec.RestoreDrawing(bytes);using var shortcut=WorkerCodec.RestoreDrawing(bytes);
        NativeVisibilityVerifier.Apply([Input(body),Input(curve,1)],exhaustive,new(){Verification=new(MaxRayTests:100){ExhaustiveOffsets=true}},default);
        NativeVisibilityVerifier.Apply([Input(body),Input(curve,1)],shortcut,new(){Verification=new(MaxRayTests:100)},default);
        Assert.Equal(WorkerCodec.CaptureDrawing(exhaustive,true),WorkerCodec.CaptureDrawing(shortcut,true));
        Assert.Contains(shortcut.Diagnostics,d=>d.Contains("unsampled interiors retain native confidence",StringComparison.Ordinal));
    });

    [RhinoFact] public void CurvedRayContradictionsSplitOnlyTheNeighbourhoodAndNeverFlipConfidence() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var line = new LineCurve(new Point3d(-2,0,-2),new Point3d(2,0,-2));
        using var output = Output();
        using var audit = new NativeVisibilityVerifier([Input(sphere)],output,new(),default);
        var spans = audit.Verify(line,LineClass.Visible,"line",default);
        Assert.Contains(spans,s => s.Class == LineClass.Visible);
        Assert.Contains(spans,s => s.Class == LineClass.Unresolved);
        Assert.DoesNotContain(spans,s => s.Class == LineClass.Hidden);
        Assert.Equal(line.Domain.Min,spans[0].A); Assert.Equal(line.Domain.Max,spans[^1].B);
        Assert.All(spans.Zip(spans.Skip(1)),p => Assert.Equal(p.First.B,p.Second.A));
        Assert.Contains(output.Diagnostics,s => s.Contains("native=Visible") && s.Contains("gap="));
        using var clear = new LineCurve(new Point3d(2,0,-2),new Point3d(3,0,-2));
        Assert.All(audit.Verify(clear,LineClass.Hidden,"clear",default),s => Assert.Equal(LineClass.Unresolved,s.Class));
    });

    [RhinoFact] public void VerifierChecksSelfOcclusionAndDoesNotSkipTheSourceBody() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var output = Output();
        using var audit = new NativeVisibilityVerifier([Input(sphere)],output,new(),default);
        Assert.Equal(VisibilityClass.Hidden,audit.Check(new Point3d(0,0,-1),default,out _));
        Assert.Equal(VisibilityClass.Visible,audit.Check(new Point3d(0,0,1),default,out _));
    });

    [RhinoFact] public void RepeatedAuditsHaveIdenticalSpansAndPreserveIndependentDepthGeometry() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var line = new LineCurve(new Point3d(-2,0,-2),new Point3d(2,0,-2));
        NativeVisibilityVerifier.Span[] Run()
        {
            using var output = Output();
            using var audit = new NativeVisibilityVerifier([Input(sphere)],output,new(),default);
            return audit.Verify(line,LineClass.Visible,"line",default);
        }
        Assert.Equal(Run(),Run());
        using var result = Output();
        using var projected = line.DuplicateCurve(); projected.Transform(Transform.PlanarProjection(Plane.WorldXY));
        result.Pieces.Add(new(projected.DuplicateCurve(),Input(line,1).Source,LineClass.Visible,line.DuplicateCurve()));
        NativeVisibilityVerifier.Apply([Input(sphere),Input(line,1)],result,new(),default);
        Assert.Contains(result.Items,p => p.Class == LineClass.Unresolved);
        double length = 0;
        foreach (var piece in result.Items)
        {
            using var g = piece.CopyGeometry(); var c = Assert.IsAssignableFrom<Curve>(g);
            var depth = Assert.IsAssignableFrom<Curve>(piece.DepthGeometry);
            Assert.Equal(-2,depth.PointAtStart.Z,10); Assert.Equal(0,c.PointAtStart.Z,10);
            Assert.Equal(depth.Domain,c.Domain); length += c.GetLength();
        }
        Assert.Equal(4,length,10);
    });

    [RhinoFact] public void PlanarContactBandIsClearAndForegroundBeyondToleranceIsHidden() => host.Run(() =>
    {
        using var box = new BoundingBox(new Point3d(-1,-1,-1),new Point3d(1,1,0)).ToBrep();
        using var output = Output();
        using var audit = new NativeVisibilityVerifier([Input(box)],output,new(),default);
        Assert.Equal(VisibilityClass.Visible,audit.Check(Point3d.Origin,default,out _));
        Assert.Equal(VisibilityClass.Visible,audit.Check(new Point3d(0,0,-.5e-6),default,out _));
        Assert.Equal(VisibilityClass.Hidden,audit.Check(new Point3d(0,0,-2e-6),default,out _));
    });

    [RhinoFact] public void MeshRayDoesNotLetAContactHitMaskAForegroundSurface() => host.Run(() =>
    {
        using var mesh = new Mesh();
        foreach (double z in new[] {0.0,1.0})
        {
            int i = mesh.Vertices.Count;
            mesh.Vertices.Add(-1,-1,z); mesh.Vertices.Add(1,-1,z); mesh.Vertices.Add(1,1,z); mesh.Vertices.Add(-1,1,z);
            mesh.Faces.AddFace(i,i+1,i+2,i+3);
        }
        // Explicitly welded folded mesh forces the native policy; planar-mesh extraction is tested elsewhere.
        mesh.Vertices.Add(0,0,2); mesh.Faces.AddFace(4,5,8);
        using var output = Output();
        using var audit = new NativeVisibilityVerifier([Input(mesh)],output,new(),default);
        Assert.Equal(VisibilityClass.Hidden,audit.Check(Point3d.Origin,default,out _));
    });

    [RhinoFact] public void BudgetExhaustionIsDeterministicAndReportedWithoutAFalseClearRay() => host.Run(() =>
    {
        using var sphere = new Sphere(Point3d.Origin,1).ToBrep();
        using var line = new LineCurve(new Point3d(-.5,0,-2),new Point3d(.5,0,-2));
        using var output = Output();
        using var audit = new NativeVisibilityVerifier([Input(sphere)],output,new(MaxRayTests:0),default);
        Assert.Equal(VisibilityClass.Unresolved,audit.Check(new Point3d(0,0,-2),default,out string reason));
        Assert.Equal("ray budget",reason);
        Assert.All(audit.Verify(line,LineClass.Visible,"line",default),s => Assert.Equal(LineClass.Visible,s.Class));
        audit.Report(); Assert.Contains(output.Diagnostics,s => s.Contains("skipped=") && s.Contains("not a visibility certificate"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => audit.Verify(line,LineClass.Visible,"line",cancelled.Token));
    });

    [RhinoTheory]
    [InlineData(17)]
    [InlineData(45)]
    public void ObliqueTwoBoxControlsRetainAnalyticClassesAndNeverCallNative(int angle) => host.Run(() =>
    {
        using var a = new BoundingBox(new Point3d(-2,-1,-1),new Point3d(2,1,2)).ToBrep();
        using var b = new BoundingBox(new Point3d(-1,-2,0),new Point3d(1,2,1)).ToBrep();
        var plane = new Plane(Point3d.Origin,Vector3d.XAxis,Vector3d.ZAxis); plane.Rotate(angle*Math.PI/180,Vector3d.ZAxis);
        using var output = Output(plane);
        ComposedVisibility.Solve([Input(a),Input(b,1)],output,ViewRequest.Parse(-plane.ZAxis),new(),default);
        Assert.Equal(0,output.NativeCalls); Assert.DoesNotContain(output.Diagnostics,s => s.Contains("Native ray"));
        foreach (double z in new[] {0.0,1.0})
        {
            var witnesses = angle == 45 ? new[] {(new Point3d(1,-1.5,z),LineClass.Visible),(new Point3d(1,-.5,z),LineClass.Hidden)} : [(new Point3d(1,-1.5,z),LineClass.Visible)];
            foreach (var (point,expected) in witnesses)
            {
                var projected = plane.ClosestPoint(point);
                var found = output.Items.Where(p => p.Class == expected).Any(p =>
                {
                    using var g = p.CopyGeometry();
                    return g is Curve c && c.ClosestPoint(projected,out double t) && c.PointAt(t).DistanceTo(projected) < 4e-6;
                });
                Assert.True(found,$"{angle} degrees at {point}: expected {expected}");
            }
        }
    });
}
