using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class HldSectionAcceptanceTests(RhinoHost host)
{
    [RhinoTheory]
    [InlineData("rhs_hollow",2.04,2)]
    [InlineData("chs_hollow",.51*Math.PI,2)]
    [InlineData("brep_box",4,1)]
    public void WorkerSectionsRetainTopologyAreaFillAndPresentation(string fixture,double expectedArea,int loops) => host.Run(() =>
    {
        using var doc=RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory,"fixtures",fixture+".3dm"));
        doc.ModelAbsoluteTolerance=.0001;
        using var cache=new SceneCache();using var scene=BaselineTests.Capture(doc,cache);
        var view=EngineNativeTests.View("hidden=on\nhatch=black\ntolerance=0.0001",Plane.WorldXY);
        var identity=new RequestIdentity(scene.Value.Revision,view.Revision,"hld-sections",1);
        using var local=NativeDrawing.SolveBorrowedAsync(scene.Value,identity,[view],new(),default,null).GetAwaiter().GetResult();
        using var pool=new DrawingWorkerPool(new(){Count=1,Directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/M2DPlus.Worker/bin/Debug/net"+Environment.Version.Major+".0-windows"))});
        using var worker=pool.SolveAsync(scene.Value,identity,[view],new(),default).GetAwaiter().GetResult();
        var target=new Plane(new Point3d(100,20,30),Vector3d.YAxis,Vector3d.ZAxis);
        using var presented=DrawingPresentation.Apply(worker,identity,[view],[target]);
        foreach(var drawing in new[]{local.Views[0],worker.Views[0],presented.Views[0]})
        {
            Assert.Null(drawing.Error);Assert.False(drawing.Report.Quarantined,drawing.Info);
            var region=Assert.Single(drawing.Regions).Region;
            Assert.Equal(loops,region.Loops.Count);Assert.Equal(loops-1,region.Loops.Count(l=>l.IsHole));
            Assert.All(region.Loops,l=>Assert.True(l.Curve.IsClosed));Assert.NotNull(region.Hatch);
            Assert.InRange(Math.Abs(region.Area-expectedArea),0,.0001);
            using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
            Assert.Equal(WorkerCodec.CaptureDrawing(drawing),WorkerCodec.CaptureDrawing(restored));
        }
        Assert.Equal(target.Origin,presented.Views[0].Frame.Plane.Origin);
        HldFixtureComparison.Equal(local.Views[0],worker.Views[0]);
    });
}
