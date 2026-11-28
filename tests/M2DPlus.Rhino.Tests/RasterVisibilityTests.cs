using M2DPlus.Core;
using M2DPlus.Core.Experimental.RasterVisibility;
using M2DPlus.Rhino.Experimental.RasterVisibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class RasterVisibilityTests(RhinoHost host)
{
    private static ViewRequest View(PreparedScene scene, object source, string engine = "raster", string extra = "")
    {
        var v = ViewRequest.Parse(source);
        return v with { Settings = Settings.Resolve([new("raster-test", v.Name, v.Kind, v.Cut is not null,
            scene.Bounds.Diagonal.Length, scene.Tolerance, v.Cut?.SourceDepth)],
            [[$"visibility={engine}\nhidden=on\nbeyond=off\nfarclip=none\n{extra}"]], DocumentUnits.Millimetres).Views[0] };
    }
    private static DrawingBatch Solve(PreparedScene scene, ViewRequest view, NativeSettings? settings = null) =>
        NativeDrawing.SolveBorrowedAsync(scene, new(scene.Revision,"raster-test","settings",1),[view],
            settings ?? new() { IsWorker = true, Scheduler = new(6*WorkerMemoryPolicy.GiB) },default,null).GetAwaiter().GetResult();
    private static double Length(ViewDrawing drawing, string identity, LineClass kind) => drawing.Items
        .Where(p=>p.SourceIdentity==identity && p.Class==kind).Sum(p=>{using var g=p.CopyGeometry(); return g is Curve c ? c.GetLength() : 0;});

    [RhinoTheory]
    [InlineData("brep_box", "Top")]
    [InlineData("brep_box", "SW")]
    [InlineData("brep_curved_sphere", "SW")]
    [InlineData("chs_hollow", "SW")]
    [InlineData("brep_trimmed_planar_annulus", "SW")]
    [InlineData("mesh_closed_quad", "Top")]
    [InlineData("mesh_open_quad", "SW")]
    [InlineData("block_nested_mirrored", "SW")]
    [InlineData("subd_rounded_cube", "SW")]
    public void PreparedFixtureViewsUseNoHldAndRoundTrip(string fixture, string direction) => host.Run(()=>
    {
        using var doc=RhinoDoc.OpenHeadless(Path.Combine(AppContext.BaseDirectory,"fixtures",fixture+".3dm"));
        Assert.NotNull(doc); using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache);
        var before=NativeDrawing.HldCalls; using var batch=Solve(scene.Value,View(scene.Value,direction));
        var drawing=Assert.Single(batch.Views); Assert.Null(drawing.Error); Assert.True(drawing.RasterRan,drawing.Info);
        Assert.Equal(before,NativeDrawing.HldCalls); Assert.Equal(0,drawing.NativeCalls); Assert.NotEmpty(drawing.Items);
        Assert.Contains("Raster (experimental)",drawing.EngineSummary); Assert.Contains("visibility not certified",drawing.Info);
        Assert.All(drawing.Items,p=>Assert.NotNull(p.DepthGeometry));
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
        Assert.Equal(WorkerCodec.CaptureDrawing(drawing),WorkerCodec.CaptureDrawing(restored));
        Assert.Contains(ViewNotices.For(drawing),n=>n.Level==NoticeLevel.Remark && n.Text.Contains("Raster (experimental)"));
    });

    [RhinoFact] public void ShallowWiresAndPointsUseTheirOwnRayAndRespectHiddenOff() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        using var box=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var buried=new LineCurve(new Point3d(1,5,9.7),new Point3d(9,5,9.7));
        using var front=new LineCurve(new Point3d(1,5,10.3),new Point3d(9,5,10.3));
        using var cross=new LineCurve(new Point3d(-1,5,5),new Point3d(11,5,5));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,
            [new(box,[],"body"),new(buried,[],"buried"),new(front,[],"front"),new(cross,[],"cross"),
             new(new Point3d(5,5,9.7),[],"hidden-point"),new(new Point3d(5,5,10.3),[],"front-point")]);
        var before=NativeDrawing.HldCalls; var view=View(scene.Value,"Top"); using var batch=Solve(scene.Value,view); var drawing=batch.Views[0];
        Assert.True(drawing.RasterRan,drawing.Info); Assert.Equal(before,NativeDrawing.HldCalls);
        Assert.Equal(8,Length(drawing,"buried",LineClass.Hidden),3); Assert.Equal(0,Length(drawing,"buried",LineClass.Visible));
        Assert.Equal(8,Length(drawing,"front",LineClass.Visible),3);
        Assert.InRange(Length(drawing,"cross",LineClass.Visible),1.99,2.01);
        Assert.DoesNotContain(drawing.Items,p=>p.SourceIdentity=="hidden-point"); Assert.Contains(drawing.Items,p=>p.SourceIdentity=="front-point");
        using var off=Solve(scene.Value,View(scene.Value,"Top",extra:"hidden=off\nmin visible=100"));
        Assert.DoesNotContain(off.Views[0].Items,p=>p.Class==LineClass.Hidden);
        using var presentation=DrawingPresentation.Apply(off,new("s","v","f",1),[View(scene.Value,"Top",extra:"hidden=off\nmin visible=100")],[Plane.WorldYZ]);
        Assert.Equal(0,presentation.Views[0].IgnoredPieces); Assert.True(presentation.Views[0].RasterRan);
    });

    [RhinoFact] public void PhysicalSectionsCropAndCutOnlyKeepTheirOrdinaryContracts() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        var box=new Box(Plane.WorldXY,new Interval(-1,11),new Interval(-1,11),new Interval(1,5));
        var view=View(scene.Value,box,extra:"farclip=line"); var before=NativeDrawing.HldCalls;
        using var batch=Solve(scene.Value,view); var drawing=batch.Views[0]; Assert.Null(drawing.Error); Assert.True(drawing.RasterRan,drawing.Info);
        Assert.Equal(before,NativeDrawing.HldCalls); Assert.NotEmpty(drawing.Regions); Assert.Equal(100,drawing.Regions.Sum(r=>r.Region.Area),3);
        Assert.All(drawing.Items,p=>{using var g=p.CopyGeometry(); Assert.All(g.GetBoundingBox(true).GetCorners(),q=>Assert.True(NativeDrawing.Inside(q,drawing.Frame,.002)));});
        using var sections=Solve(scene.Value,View(scene.Value,box,extra:"farclip=line\ncutonly=on"));
        Assert.False(sections.Views[0].RasterRan); Assert.Equal(before,NativeDrawing.HldCalls); Assert.Empty(sections.Views[0].Items);
        Assert.Equal(drawing.Regions.Count,sections.Views[0].Regions.Count);
        using var presented=DrawingPresentation.Apply(batch,new("s","v","f",1),[view],[Plane.WorldYZ]);
        Assert.True(presented.Views[0].RasterRan); Assert.Equal(drawing.RasterPixel,presented.Views[0].RasterPixel);
        var bake=DrawingBake.Bake(doc,Guid.NewGuid(),[new BakeDrawing(presented.Views[0],"raster-test")]); Assert.NotEmpty(bake.Added);
    });

    [RhinoFact] public void RefusedProjectionFallsBackForTheEntireBatchAndHldRequestsUseUnchangedAdapter() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        var view=View(scene.Value,"SW"); var frame=view.Resolve(scene.Value.Bounds,.001);
        var input=scene.Value.Prepare(default).Select(s=>new VisibilityInput(s.Geometry,s)).ToArray();
        using var fallback=new ViewDrawing {Frame=frame,Tolerance=.001,Settings=view.Settings};
        RasterProjectionRoute.Solve(input,fallback,view,new() {Scheduler=new(1024*1024)},scene.Value,default);
        Assert.Equal(1,fallback.NativeCalls); Assert.False(fallback.RasterRan);
        Assert.Contains(fallback.Diagnostics,d=>d.StartsWith("raster engine:") && d.Contains("whole view")); Assert.NotEmpty(fallback.Items);
        var hld=View(scene.Value,"SW","hld"); using var actual=new ViewDrawing{Frame=frame,Tolerance=.001,Settings=hld.Settings};
        using var expected=new ViewDrawing{Frame=frame,Tolerance=.001,Settings=hld.Settings};
        RasterProjectionRoute.Solve(input,actual,hld,new(),scene.Value,default);
        HldProjection.Solve(input,expected,hld,new(),scene.Value.Bounds,default);
        Assert.Equal(1,actual.NativeCalls); Assert.False(actual.RasterRan); HldFixtureComparison.Equal(expected,actual);
        Assert.DoesNotContain(actual.Diagnostics,d=>d.StartsWith("raster engine:"));
    });

    [RhinoFact] public void AnalyticCylinderSilhouettesStayContinuousAtSurveyCoordinates() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        var axis=new Vector3d(1,.3,.2); axis.Unitize(); var side=Vector3d.CrossProduct(Vector3d.ZAxis,axis); side.Unitize();
        foreach(var offset in new[]{0d,1_000_000d})
        {
            var origin=new Point3d(offset,offset,offset);
            using var body=new Cylinder(new Circle(new Plane(origin,axis),50),1200).ToBrep(true,true);
            using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"cylinder")]);
            using var batch=Solve(scene.Value,View(scene.Value,"Top")); var drawing=batch.Views[0]; Assert.True(drawing.RasterRan,drawing.Info);
            var curves=drawing.Items.Where(p=>p.Class==LineClass.Visible).Select(p=>(Curve)p.CopyGeometry()).ToArray();
            try
            {
                foreach(var sign in new[]{-1,1})
                for(var i=1;i<50;i++)
                {
                    var point=drawing.Frame.Plane.ClosestPoint(origin+axis*(1200*i/50d)+side*(50*sign));
                    Assert.Contains(curves,c=>c.ClosestPoint(point,out var t) && c.PointAt(t).DistanceTo(point)<.005);
                }
            }
            finally{foreach(var c in curves)c.Dispose();}
        }
    });

    [RhinoFact] public void MissingWorkersPermitOnlyTheOrdinarySmallSceneHldFallback() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        using var unavailable=new DrawingWorkerPool(new(){Count=1,Directory=Path.Combine(AppContext.BaseDirectory,"no-raster-workers")});
        Assert.False(unavailable.Installed);
        var settings=new NativeSettings{Scheduler=new(100*RasterPolicy.MiB)};
        Assert.True(scene.Value.Cost.Admitted(1,settings.Scheduler.BudgetBytes));
        Assert.True(RasterPolicy.Reservation(scene.Value.Cost)>settings.Scheduler.BudgetBytes);
        using var batch=DrawingExecution.SolveBorrowedAsync(scene.Value,new(scene.Value.Revision,"v","r",1),
            [View(scene.Value,"SW")],settings,default,null,unavailable).GetAwaiter().GetResult();
        var drawing=batch.Views.Single(); Assert.Null(drawing.Error); Assert.False(drawing.RasterRan); Assert.Equal(1,drawing.NativeCalls);
        Assert.Contains(drawing.Diagnostics,d=>d.StartsWith("raster engine:") && d.Contains("memory admission"));
        Assert.DoesNotContain(ViewNotices.For(drawing),n=>n.Level==NoticeLevel.Warning);
    });

    [RhinoFact] public void BorrowedMeshesRemainUnchangedAndCancellationNeverStartsFallback() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Millimeters; doc.ModelAbsoluteTolerance=.001;
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var mesh=new Mesh(); var parts=Mesh.CreateFromBrep(body,MeshingParameters.FastRenderMesh);
        try {foreach(var part in parts)mesh.Append(part);} finally {foreach(var part in parts)part.Dispose();}
        mesh.FaceNormals.Clear();
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(mesh,[],"mesh")]);
        var prepared=Assert.IsType<Mesh>(Assert.Single(scene.Value.Prepare(default)).Geometry); prepared.FaceNormals.Clear();
        var hash=GeometryFingerprint.Compute(prepared); var view=View(scene.Value,"SW");
        using var drawing=Solve(scene.Value,view); Assert.True(drawing.Views[0].RasterRan,drawing.Views[0].Info);
        Assert.Equal(hash,GeometryFingerprint.Compute(prepared)); Assert.Empty(prepared.FaceNormals);
        var calls=NativeDrawing.HldCalls; using var cancel=new CancellationTokenSource(); cancel.Cancel();
        using var output=new ViewDrawing{Frame=view.Resolve(scene.Value.Bounds,.001),Tolerance=.001};
        Assert.ThrowsAny<OperationCanceledException>(()=>RasterProjectionRoute.Solve([new(prepared,scene.Value.Prepare(default).Single())],
            output,view,new(),scene.Value,cancel.Token));
        Assert.Equal(calls,NativeDrawing.HldCalls); Assert.Empty(output.Items);
    });

    [RhinoFact] public void WorkerAndMacRoutesCarryRasterSettingsCountersAndUnits() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem=UnitSystem.Meters; doc.ModelAbsoluteTolerance=.00001;
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(1,2,3)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        Assert.Equal(1000,scene.Value.MillimetresPerUnit);
        var view=View(scene.Value,"SW"); using var local=Solve(scene.Value,view);
        using var restoredScene=WorkerCodec.RestoreScene(WorkerCodec.Capture(scene.Value,default)); Assert.Equal(1000,restoredScene.MillimetresPerUnit);
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+Environment.Version.Major+".0-windows"));
        using var pool=new DrawingWorkerPool(new(){Count=1,Directory=directory});
        using var firstHld=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"hld","hld",0),[View(scene.Value,"SW","hld")],new(),default).GetAwaiter().GetResult();
        Assert.Null(firstHld.Views[0].Error);
        var hldProcesses=pool.ProcessIds;
        var sceneTransfers=new SceneTransfers();
        using var worker=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"raster","raster",1),[view],new(),default,sceneTransfers).GetAwaiter().GetResult();
        Assert.Equal(hldProcesses,pool.ProcessIds); Assert.Equal(0,sceneTransfers.Count);
        Assert.Null(worker.Views[0].Error); Assert.True(worker.Views[0].RasterRan,worker.Views[0].Info); Assert.Equal(0,worker.Views[0].NativeCalls);
        Assert.Equal(local.Views[0].RasterPixel,worker.Views[0].RasterPixel); Assert.Equal(local.Views[0].RasterTriangles,worker.Views[0].RasterTriangles);
        // Units changed without scaling geometry or tolerance: keep HLD identities stable,
        // but reload Raster's unit metadata in an already running worker and visibility cache.
        using var drawingCache=new DrawingCache();
        using var cachedFirst=drawingCache.SolveAsync(BaselineTests.Capture(doc,cache,[new(body,[],"body")]),
            new(scene.Value.Revision,"units","raster",1),[view],new(),[],default).GetAwaiter().GetResult();
        var processes=pool.ProcessIds;
        doc.ModelUnitSystem=UnitSystem.Centimeters;
        using var nextScene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        Assert.Equal(scene.Value.Revision,nextScene.Value.Revision); // No change to HLD scene identity.
        var nextView=View(nextScene.Value,"SW");
        using var reloaded=pool.SolveAsync(nextScene.Value,new(nextScene.Value.Revision,"raster","raster",2),[nextView],new(),default).GetAwaiter().GetResult();
        Assert.Equal(processes,pool.ProcessIds); Assert.True(reloaded.Views[0].RasterRan,reloaded.Views[0].Info);
        Assert.Equal(worker.Views[0].RasterPixel/100,reloaded.Views[0].RasterPixel,8);
        using var cachedNext=drawingCache.SolveAsync(BaselineTests.Capture(doc,cache,[new(body,[],"body")]),
            new(nextScene.Value.Revision,"units","raster",2),[nextView],new(),[],default).GetAwaiter().GetResult();
        Assert.Equal(cachedFirst.Views[0].RasterPixel/100,cachedNext.Views[0].RasterPixel,8);
        var original=HostPlatform.OverrideForTests; HostPlatform.OverrideForTests=HostOs.MacOS;
        try
        {
            using var mac=DrawingExecution.SolveBorrowedAsync(scene.Value,new("s","v","r",1),[view],new(){Scheduler=new(2*WorkerMemoryPolicy.GiB)},default,null).GetAwaiter().GetResult();
            Assert.True(mac.Views[0].RasterRan,mac.Views[0].Info); Assert.Equal(0,mac.Views[0].NativeCalls); Assert.Contains(mac.Views[0].Diagnostics,d=>d.Contains("in-process") || d.Contains("in Rhino's process"));
        }
        finally {HostPlatform.OverrideForTests=original;}
    });
    [RhinoFact] public void CriticalRasterFailuresEscapeTheWholePipelineWithoutHldFallback() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        foreach (var source in new object[] { "SW", new Plane(new Point3d(0,0,5), Vector3d.ZAxis) })
        foreach (var error in new Exception[] { new OutOfMemoryException("injected Raster allocation failure"),
            new AggregateException(new OutOfMemoryException("injected parallel allocation failure")), new AccessViolationException("injected critical failure") })
        {
            var calls=NativeDrawing.HldCalls;
            var settings=new NativeSettings { IsWorker=true, Scheduler=new(6*WorkerMemoryPolicy.GiB), RasterStarting=()=>throw error };
            Assert.Same(error, Assert.ThrowsAny<Exception>(()=>Solve(scene.Value,View(scene.Value,source),settings)));
            Assert.Equal(calls,NativeDrawing.HldCalls);
        }
        using var ordinary=Solve(scene.Value,View(scene.Value,"SW"),new() { IsWorker=true, Scheduler=new(6*WorkerMemoryPolicy.GiB),
            RasterStarting=()=>throw new InvalidOperationException("injected ordinary geometry failure") });
        Assert.Equal(1,ordinary.Views[0].NativeCalls); Assert.False(ordinary.Views[0].RasterRan);
        Assert.Contains(ordinary.Views[0].Diagnostics,d=>d.Contains("injected ordinary geometry failure") && d.Contains("whole view"));
        Assert.True(RasterProjectionRoute.CanFallback(new InvalidOperationException("ordinary geometry failure")));
        Assert.False(RasterProjectionRoute.CanFallback(new AggregateException(new OperationCanceledException())));
    });
    [RhinoFact] public void EmptyRasterAndThreadingCountersDescribeTheActualProjection() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);
        using var body=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(10,10,10)));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache,[new(body,[],"body")]);
        var view=View(scene.Value,"SW");
        using var empty=new ViewDrawing { Settings=view.Settings };
        RasterProjectionRoute.Solve([],empty,view,new(),scene.Value,default);
        Assert.True(empty.RasterRan); Assert.False(empty.MultipleThreads); Assert.Equal(0,empty.NativeCalls);
        foreach (var worker in new[] { false, true })
        {
            using var batch=Solve(scene.Value,view,new() { IsWorker=worker, MultipleThreads=false, Scheduler=new(6*WorkerMemoryPolicy.GiB) });
            Assert.True(batch.Views[0].RasterRan,batch.Views[0].Info); Assert.False(batch.Views[0].MultipleThreads);
        }
    });

    [RhinoFact] public void SwitchingBackToHldRetainsASufficientRasterWorkerCap() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance=.1;
        var bodies=Enumerable.Range(0,100).Select(i=>new Sphere(new Point3d(i%10*4,i/10*4,0),1).ToBrep()).ToArray();
        try
        {
            using var cache=new SceneCache();
            using var scene=BaselineTests.Capture(doc,cache,bodies.Select((b,i)=>new CaptureInput(b,[],"sphere-"+i)).ToArray());
            var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+Environment.Version.Major+".0-windows"));
            using var pool=new DrawingWorkerPool(new(){Count=1,Directory=directory});
            Assert.True(RasterPolicy.WorkerExtra(scene.Value.Cost)>0);
            using var first=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"v","hld",1),[View(scene.Value,"SW","hld")],new(),default).GetAwaiter().GetResult();
            Assert.Null(first.Views[0].Error); var hldCap=pool.LastSizing!.CapBytes;
            using var raster=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"v","raster",2),[View(scene.Value,"SW")],new(),default).GetAwaiter().GetResult();
            Assert.True(raster.Views[0].RasterRan,raster.Views[0].Info);
            var processes=pool.ProcessIds; var rasterCap=pool.LastSizing!.CapBytes; Assert.True(rasterCap>hldCap);
            foreach(var engine in new[]{"hld","raster"})
            {
                using var again=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"v",engine,3),[View(scene.Value,"SW",engine)],new(),default).GetAwaiter().GetResult();
                Assert.Null(again.Views[0].Error); Assert.Equal(engine=="raster",again.Views[0].RasterRan);
                Assert.Equal(processes,pool.ProcessIds); Assert.Equal(rasterCap,pool.LastSizing!.CapBytes);
                Assert.True(pool.LastSizing.Count*pool.LastSizing.CapBytes+pool.LastSizing.HostReplyBytes<=pool.LastSizing.BudgetBytes);
            }
        }
        finally { foreach(var body in bodies)body.Dispose(); }
    });
    private sealed class SceneTransfers : IProgress<JobProgress>
    {
        internal int Count;
        public void Report(JobProgress progress) { if (progress.Step == "sending scene") Interlocked.Increment(ref Count); }
    }

}
