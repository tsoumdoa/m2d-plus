using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Core.Output;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
namespace M2DPlus.Rhino.Tests;
[Collection("Rhino")]
public sealed class ContactDrawingTests(RhinoHost host)
{
    [RhinoFact] public void CoincidentSourcesRetainNativeOwnershipAndTransportPaths() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);using var box=new BoundingBox(Point3d.Origin,new Point3d(2,2,2)).ToBrep();
        using var cache=new SceneCache();using var scene=BaselineTests.Capture(doc,cache,[new(box,[3],"a"),new(box,[7],"b")]);
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",1),[EngineNativeTests.View()],new(),default,null).GetAwaiter().GetResult();
        var result=batch.Views[0];Assert.Null(result.Error);Assert.NotEmpty(result.Items);Assert.Equal(1,result.NativeCalls);
        Assert.All(result.Items,p=>Assert.Equal(p.SourceIdentity=="a"?new[]{3}:new[]{7},p.SourcePath));
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(result));
        Assert.Equal(WorkerCodec.CaptureDrawing(result),WorkerCodec.CaptureDrawing(restored));
        Assert.Contains("duplicate ownership/source attribution",result.Info);
        foreach(var piece in result.Items)
        {
            var group=OutputTree.Group(SourceGroupInfo.Grasshopper(piece.SourcePath,piece.SourceItem),OutputTreeMode.Object);
            Assert.Equal(piece.SourcePath.Append(piece.SourceItem),group.Path);
        }
        var baked=DrawingBake.Bake(doc,Guid.NewGuid(),[new BakeDrawing(result,"Coincident")]);
        Assert.Equal(result.Items.Count,baked.Added.Count);
        Assert.All(baked.Added,id=>Assert.Contains(doc.Objects.FindId(id).Attributes.GetUserString(BakeTags.Source),new[]{"a","b"}));
    });
    [RhinoFact] public void SectionCutsAndHatchesThroughFlushAndSeatedContactsIgnoreJoints() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        Brep Box(double x0,double y0,double z0,double x1,double y1,double z1)=>
            new BoundingBox(new Point3d(x0,y0,z0),new Point3d(x1,y1,z1)).ToBrep();
        using var a=Box(-2,0,0,0,2,2);using var b=Box(0,0,0,2,2,2);using var slab=Box(-3,-1,-1,3,3,0);
        doc.Objects.AddBrep(a);doc.Objects.AddBrep(b);doc.Objects.AddBrep(slab);
        using var cache=new SceneCache();using var lease=BaselineTests.Capture(doc,cache);
        var section=EngineNativeTests.View("hidden=on\nbeyond=off\ndepth=5\nhatch=black",new Plane(new Point3d(0,1,0),Vector3d.XAxis,Vector3d.ZAxis));
        using var batch=NativeDrawing.SolveBorrowedAsync(lease.Value,new("s","contacts","f",1),[section],new(),default,null).GetAwaiter().GetResult();
        var view=batch.Views[0];Assert.Null(view.Error);Assert.False(view.Report.Quarantined);
        Assert.Equal(3,view.Regions.Count);Assert.All(view.Regions,r=>{Assert.NotNull(r.Region.Hatch);Assert.Single(r.Region.Loops);Assert.True(r.Region.Loops[0].Curve.IsClosed);});
        Assert.Equal(new[]{4d,4d,6d},view.Regions.Select(r=>r.Region.Area).Order());
        // The touching horizontal faces are contacts, not crossings and cannot acquire Cut/Hatch.
        using var contact=NativeDrawing.SolveBorrowedAsync(lease.Value,new("s","contacts","f",2),[EngineNativeTests.View("",Plane.WorldXY)],new(),default,null).GetAwaiter().GetResult();
        Assert.Empty(contact.Views[0].Regions);
    });
}
