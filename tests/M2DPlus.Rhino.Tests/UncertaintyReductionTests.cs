using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Rhino;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using Point = Rhino.Geometry.Point;
using Xunit;
namespace M2DPlus.Rhino.Tests;
[Collection("Rhino")]
public sealed class UncertaintyReductionTests(RhinoHost host)
{
    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void FailureDemotesWithoutRestoringProofsAndSuppressesAffectedPoints(bool whole) => host.Run(() =>
    {
        using var line=new LineCurve(new Point3d(-2,0,0),new Point3d(2,0,0));
        using var point=new Point(Point3d.Origin);var source=new PreparedLeaf("source",[0],line);
        using var drawing=new ViewDrawing{Frame=new(Plane.WorldXY,10,10),Tolerance=1e-6};
        drawing.Pieces.Add(new(line.Duplicate(),source,LineClass.Visible,line.Duplicate()));
        drawing.Pieces.Add(new(point.Duplicate(),source,LineClass.Visible,point.Duplicate()));
        HldProjection.ApplyFailure(drawing,whole,[new(new Point3d(-1,-1,-1),new Point3d(1,1,1))],1e-6,default);
        Assert.Equal(LineClass.Unresolved,Assert.Single(drawing.Items).Class);
        Assert.Equal(LineClass.Visible,drawing.Items[0].SolvedClass);
    });
    [RhinoFact] public void CertifiedMaterialRegionSurvivesAnotherSourcesAmbiguousSection() => host.Run(() =>
    {
        using var document=RhinoDoc.CreateHeadless(null);document.ModelAbsoluteTolerance=1e-4;
        using var box=Brep.CreateFromBox(new BoundingBox(new Point3d(-2,-2,-1),new Point3d(2,2,1)));
        using var tangent=new Sphere(new Point3d(0,0,1-2e-4),1).ToBrep();
        using var inside=new LineCurve(new Point3d(-.2,0,-2),new Point3d(.2,0,-2));
        using var outside=new LineCurve(new Point3d(3,3,-2),new Point3d(4,3,-2));
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(document,cache,
            [new(box,[0],"certified"),new(tangent,[1],"ambiguous"),new(inside,[2],"inside"),new(outside,[3],"outside")]);
        using var batch=NativeDrawing.SolveBorrowedAsync(lease.Value,new("s","v","f",1),[ViewRequest.Parse(Plane.WorldXY)],new(),default,null).GetAwaiter().GetResult();
        var view=Assert.Single(batch.Views);Assert.Null(view.Error);Assert.False(view.Report.Quarantined,view.Info);
        var region=Assert.Single(view.Regions).Region;Assert.Equal(new[]{"certified"},region.SourceIds);
        Assert.Equal(16,region.Area,8);Assert.Single(region.Loops);Assert.NotNull(region.Hatch);
        Assert.Contains(view.Diagnostics,d=>d.Contains("ambiguous: Uncertain:",StringComparison.Ordinal));
        Assert.DoesNotContain(view.Regions,r=>r.Region.SourceIds.Contains("ambiguous"));
        Assert.Contains(view.Items,p=>p.SourceIdentity=="outside"&&p.Class==LineClass.Visible);
        Assert.Contains(view.Items,p=>p.SourceIdentity=="inside"&&p.Class==LineClass.Unresolved);
        Assert.All(view.Items.Where(p=>p.Class==LineClass.Unresolved),p=>
        {
            using var g=p.CopyGeometry();var b=g.GetBoundingBox(true);
            Assert.True(b.Min.X<=1.001&&b.Max.X>=-1.001&&b.Min.Y<=1.001&&b.Max.Y>=-1.001,view.Info);
        });
        Assert.Contains(ViewNotices.For(view),n=>n.Level==NoticeLevel.Warning&&n.Text.Contains("ambiguous",StringComparison.Ordinal));
    });
    [RhinoFact] public void CutAdmissionDiagnosticsDoNotInvalidateSuccessfulVisibilityClips() => host.Run(() =>
    {
        using var box=new BoundingBox(new Point3d(-1,-1,-1),new Point3d(1,1,1)).ToBrep();
        var source=new PreparedLeaf("body",[0],box);
        using var sections=new SectionResult();sections.Visibility.Add("body",new PhysicalClipResult());
        sections.Diagnostics.Add(new("body",SectionStatus.Uncertain,"Cut component/merge refusal, with a certified visibility clip."));
        using var view=new ViewDrawing();
        Assert.Empty(SectionPreparation.FailedClipFootprints([source],sections,Plane.WorldXY,view));
        Assert.Empty(view.Diagnostics);Assert.False(view.RoutingQuarantine);
        sections.Visibility["body"].Diagnostics.Add(new("body",SectionStatus.Unsupported,"Physical trim failed."));
        Assert.NotEmpty(SectionPreparation.FailedClipFootprints([source],sections,Plane.WorldXY,view));
        Assert.False(view.RoutingQuarantine);Assert.Contains(ViewNotices.For(view),n=>n.Level==NoticeLevel.Warning&&n.Text.Contains("body",StringComparison.Ordinal));
    });
    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void RejectedSourceDoesNotDemoteItsNeighbours(bool throws) => host.Run(() =>
    {
        using var line=new LineCurve(Point3d.Origin,new Point3d(1,0,0));var input=new VisibilityInput(line,new("rejected",[0],line));
        using var output=new ViewDrawing();output.Pieces.Add(new(line.Duplicate(),input.Source,LineClass.Visible));
        Assert.False(HldProjection.Register(input,_=>throws?throw new InvalidOperationException("Rejected"):false,output,default));
        Assert.Equal(1,output.RoutingInvalid);Assert.False(output.RoutingQuarantine);
        Assert.Equal(LineClass.Visible,Assert.Single(output.Items).Class);
        Assert.Contains(ViewNotices.For(output),n=>n.Level==NoticeLevel.Warning&&n.Text.Contains("rejected",StringComparison.Ordinal));
    });
    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void DetachedUnsetRemainsLocalAndRetainsFarProvenance(bool far) => host.Run(() =>
    {
        using var line=new LineCurve(Point3d.Origin,new Point3d(1,0,0));var input=new VisibilityInput(line,new("unset",[0],line),far);
        using var output=new ViewDrawing{Frame=new(Plane.WorldXY,10,10)};
        output.Pieces.Add(new(line.DuplicateCurve(),input.Source,LineClass.Visible));
        Assert.True(HldProjection.RetainDetachedUnset(output,input,line.DuplicateCurve()));
        Assert.Equal(LineClass.Visible,output.Items[0].Class);Assert.Equal(LineClass.Unresolved,output.Items[1].Class);
        Assert.Equal(far,output.Items[1].FarCandidate);Assert.False(output.RoutingQuarantine);
        // SectionDrawing discards synthetic far candidates unless classified Visible.
        Assert.Equal(far,output.Items[1].FarCandidate&&output.Items[1].Class!=LineClass.Visible);
    });
    [RhinoFact] public void UnsetPointNeverTouchesSourceMetadataAndPointFaultsStayLocal() => host.Run(() =>
    {
        var touched=0;
        var unset=HldProjection.InspectPoint(()=>HiddenLineDrawingPoint.Visibility.Unset,()=>{touched++;throw new NullReferenceException();},()=>throw new InvalidOperationException());
        Assert.Equal(0,touched);Assert.Null(unset.Input);Assert.Contains("Unset",unset.Diagnostic);
        var missing=HldProjection.InspectPoint(()=>HiddenLineDrawingPoint.Visibility.Visible,()=>null,()=>throw new InvalidOperationException());
        Assert.Null(missing.Input);Assert.Contains("Source-less",missing.Diagnostic);
        var fault=HldProjection.InspectPoint(()=>HiddenLineDrawingPoint.Visibility.Visible,()=>throw new NullReferenceException("proxy"),()=>Point3d.Unset);
        Assert.Null(fault.Input);Assert.Contains("proxy",fault.Diagnostic);
    });
    [RhinoFact] public void NullHldDrawingIsTheWholeViewFailureBoundary() => host.Run(() =>
    {
        using var line=new LineCurve(Point3d.Origin,new Point3d(1,0,0));using var view=new ViewDrawing();
        view.Pieces.Add(new(line.DuplicateCurve(),new("line",[0],line),LineClass.Visible));
        Assert.False(HldProjection.TryFrame(null,view,out _));Assert.True(view.RoutingQuarantine);
        Assert.Equal(LineClass.Unresolved,Assert.Single(view.Items).Class);
    });
}
