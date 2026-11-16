using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class EngineFastTests(RhinoHost host)
{
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc doc,SceneCache cache)
    {
        using var watch=new DocumentWatch(doc);using var capture=new SceneCapture(doc,watch,null,cache);
        while(!capture.Step(TimeSpan.FromSeconds(30))){}return capture.Accept();
    }
    private static ViewRequest View(bool fast,bool hidden,bool cut=false,double minVisible=0,bool forSolve=true)
    {
        var view=cut?ViewRequest.Parse(new Plane(Point3d.Origin,Vector3d.ZAxis)):ViewRequest.Parse("Back");
        var setting=Settings.Resolve([new("v",view.Name,view.Kind,cut,20,1e-6)],
            [[$"engine={(fast?"fast":"exact")}\nhidden={(hidden?"on":"off")}\nmin visible={minVisible}"]],DocumentUnits.Metres).Views[0];
        return view with{Settings=forSolve?setting.ForVisibilitySolve():setting};
    }
    private static Brep Freeform()
    {
        var points=Enumerable.Range(0,4).SelectMany(i=>Enumerable.Range(0,4).Select(j=>
        {double x=-1+2*i/3d,y=-1+2*j/3d;return new Point3d(x,y,.3*x*y+.1*x*x*y*y);}));
        using var surface=NurbsSurface.CreateFromPoints(points,4,4,3,3);return surface.ToBrep();
    }

    [RhinoTheory]
    [InlineData(false,false,false)] [InlineData(false,true,false)]
    [InlineData(true,false,false)] [InlineData(true,true,false)]
    [InlineData(true,false,true)] [InlineData(true,true,true)]
    public void NativeCallsFollowEngineHiddenAndRoutePolicy(bool fast,bool hidden,bool nativeOnly) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        // A bare freeform patch takes the ordinary native fallback. Adding a
        // line gives composition an exact candidate and exercises mixed routing.
        if(!nativeOnly)doc.Objects.AddLine(new Point3d(-2,-2,-.5),new Point3d(2,2,-.5));
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var options=new List<NativeHldOptions>();var reports=new List<JobProgress>();
        using var result=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","settings",1),[View(fast,hidden)],
            new(){ObserveHldOptions=options.Add},default,new Sink(reports)).GetAwaiter().GetResult();
        var drawing=Assert.Single(result.Views);Assert.Null(drawing.Error);Assert.NotEmpty(options);
        Assert.All(options,o=>{Assert.Equal(fast,o.MultipleThreads);Assert.Equal(!fast||hidden,o.IncludeHiddenCurves);});
        Assert.Contains(drawing.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
        if(fast)
        {
            Assert.True(drawing.FastNativeSources>0);Assert.True(drawing.MultipleThreads);
            Assert.Contains("Engine: fast — Make2D-like shortcuts on",drawing.Info);
            var notice=Assert.Single(ViewNotices.For(drawing),n=>n.Text.Contains("Engine: fast",StringComparison.Ordinal));
            Assert.Equal(NoticeLevel.Remark,notice.Level);
            var component=System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"M2DPlus.Grasshopper.gha")).GetType("M2DPlus.Grasshopper.DrawingComponent",true)!;
            var level=component.GetMethod("Level",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark,level.Invoke(null,[notice.Level]));
            Assert.Contains(reports,p=>p.Step==ViewSteps.Fast);
            if(!hidden)Assert.Contains("false-Hidden checking skipped",drawing.Info);
            using var roundTrip=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
            Assert.Equal(drawing.FastNativeSources,roundTrip.FastNativeSources);Assert.Equal(drawing.EngineSummary,roundTrip.EngineSummary);
        }
        else
        {
            Assert.Equal(0,drawing.FastNativeSources);Assert.DoesNotContain(ViewNotices.For(drawing),n=>n.Text.Contains("Engine: fast",StringComparison.Ordinal));
            Assert.DoesNotContain(reports,p=>p.Step==ViewSteps.Fast);
        }
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void RouteCNativeFallbackAlsoUsesTheFastOptions(bool hidden) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var sphere=new Sphere(new Point3d(0,0,-.25),1).ToBrep();doc.Objects.AddBrep(sphere);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);var observed=new List<NativeHldOptions>();
        using var result=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","settings",1),[View(true,hidden,cut:true)],
            new(){ObserveHldOptions=observed.Add},default,null).GetAwaiter().GetResult();
        var drawing=Assert.Single(result.Views);Assert.Null(drawing.Error);Assert.NotEmpty(observed);
        Assert.All(observed,o=>{Assert.True(o.MultipleThreads);Assert.Equal(hidden,o.IncludeHiddenCurves);});
        Assert.True(drawing.FastNativeSources>0);Assert.Contains("Engine: fast",drawing.Info);
        Assert.Contains(drawing.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void PlanarOnlyDrawingsKeepIdenticalGeometryClassesAndQualityIncludingMinVisible(bool hidden) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=new BoundingBox(new Point3d(-2,-1,-1),new Point3d(2,1,1)).ToBrep();doc.Objects.AddBrep(a);
        using var b=new BoundingBox(new Point3d(-1,2,-.5),new Point3d(1,3,.5)).ToBrep();doc.Objects.AddBrep(b);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var ev=View(false,hidden,minVisible:.1,forSolve:false);var fv=View(true,hidden,minVisible:.1,forSolve:false);
        using var exact=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","e",1),[ev with{Settings=ev.Settings!.ForVisibilitySolve()}],new(),default,null).GetAwaiter().GetResult();
        using var fast=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",2),[fv with{Settings=fv.Settings!.ForVisibilitySolve()}],new(),default,null).GetAwaiter().GetResult();
        using var exactDisplay=DrawingPresentation.Apply(exact,exact.Identity,[ev],[]);
        using var fastDisplay=DrawingPresentation.Apply(fast,fast.Identity,[fv],[]);
        var x=exactDisplay.Views[0];var y=fastDisplay.Views[0];Assert.Null(x.Error);Assert.Null(y.Error);
        Assert.Equal(0,x.NativeCalls);Assert.Equal(0,y.NativeCalls);Assert.Equal(0,y.FastNativeSources);Assert.False(y.MultipleThreads);
        // Authored engine/hidden settings differ; normalize only the settings
        // record, retaining every geometry, class, quality and routing field.
        var ex=WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(x,true));
        var fy=WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(y,true));
        Assert.Equal(x.Settings!.View,y.Settings!.View);
        foreach(var entry in x.Settings.Values.Where(p=>p.Key!="engine"))Assert.Equal(entry.Value,y.Settings[entry.Key]);
        Assert.Equal(WorkerCodec.Encode(ex),WorkerCodec.Encode(fy with{Values=fy.Values with{Settings=ex.Values.Settings}}));
        Assert.DoesNotContain(ViewNotices.For(y),n=>n.Text.Contains("Engine: fast — Make2D-like",StringComparison.Ordinal));
    });

    [RhinoFact] public void ViewObjectEngineUserTextUsesTheSamePrecedence() => host.Run(()=>
    {
        var attributes=new ObjectAttributes();attributes.SetUserString("m2d.engine","fast");
        var row=ViewBoxes.ReadRow(attributes);Assert.Equal("fast",row.Values["engine"]);
        var context=new ViewSettingsContext("v","v",ViewSourceKind.Rectangle,false,20,.001);
        var menu=new EditorSettings(new([KeyValuePair.Create("engine","exact")]),[KeyValuePair.Create("v",row)]);
        Assert.Equal(EngineMode.Fast,Settings.Resolve([context],[],DocumentUnits.Metres,menu).Views[0].Engine);
        Assert.Equal(EngineMode.Exact,Settings.Resolve([context],[["engine=exact"]],DocumentUnits.Metres,menu).Views[0].Engine);
    });

    [RhinoFact] public void DrawingCacheSeparatesEngineAndFastHiddenButReusesExactHidden() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        using var scenes=new SceneCache();using var drawings=new DrawingCache();
        DrawingBatch Draw(bool fast,bool hidden)
        {
            var lease=Capture(doc,scenes);
            var view=View(fast,hidden);
            // The presentation request must retain hidden=off; only the cache
            // normalizes the settings passed into the visibility solve.
            var context=view.Settings!.View;
            view=view with{Settings=Settings.Resolve([context],[[$"engine={(fast?"fast":"exact")}\nhidden={(hidden?"on":"off")}"]],DocumentUnits.Metres).Views[0]};
            return drawings.SolveAsync(lease,new(lease.Value.Revision,"view","settings",1),[view],new(),[],default).GetAwaiter().GetResult();
        }
        using var exactOn=Draw(false,true);var afterExact=NativeDrawing.HldCalls;
        using var exactOff=Draw(false,false);Assert.Equal(afterExact,NativeDrawing.HldCalls);
        using var fastOff=Draw(true,false);Assert.Equal(afterExact+1,NativeDrawing.HldCalls);Assert.True(fastOff.Views[0].FastNativeSources>0);
        Assert.Contains("IncludeHiddenCurves=False",fastOff.Views[0].Info);
        using var repeat=Draw(true,false);Assert.Equal(afterExact+1,NativeDrawing.HldCalls);
        using var fastOn=Draw(true,true);Assert.Equal(afterExact+2,NativeDrawing.HldCalls);
        Assert.Contains("IncludeHiddenCurves=True",fastOn.Views[0].Info);
    });

    [RhinoFact] public void ActualComponentEmitsFastInfoAndARemark() => host.Run(()=>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var rhino=RhinoDoc.CreateHeadless(null);RhinoDoc.ActiveDoc=rhino;rhino.ModelAbsoluteTolerance=1e-6;
        using var document=new global::Grasshopper.Kernel.GH_Document{Enabled=true};
        var assembly=System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"M2DPlus.Grasshopper.gha"));
        var component=(global::Grasshopper.Kernel.GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent",true)!)!;
        document.AddObject(component,false);using var body=Freeform();
        try
        {
            ((global::Grasshopper.Kernel.Parameters.Param_GenericObject)component.Params.Input[0]).PersistentData.Append(new global::Grasshopper.Kernel.Types.GH_ObjectWrapper(body));
            ((global::Grasshopper.Kernel.Parameters.Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new global::Grasshopper.Kernel.Types.GH_String("Back"));
            ((global::Grasshopper.Kernel.Parameters.Param_String)component.Params.Input[3]).PersistentData.Append(new global::Grasshopper.Kernel.Types.GH_String("engine=fast\nhidden=off"));
            document.NewSolution(true,global::Grasshopper.Kernel.GH_SolutionMode.Silent);
            GrasshopperTests.PumpUntil(()=>component.Params.Output[8].VolatileDataCount==1,()=>component.Message,
                ()=>{if(document.ScheduleDelay>=0)document.NewSolution(false,global::Grasshopper.Kernel.GH_SolutionMode.Silent);});
            var info=string.Join("\n",component.Params.Output[8].VolatileData.AllData(true).Cast<global::Grasshopper.Kernel.Types.GH_String>().Select(s=>s.Value));
            Assert.Contains("Engine: fast — Make2D-like shortcuts on",info);
            Assert.Contains("false-Hidden checking skipped",info);
            Assert.Contains(component.RuntimeMessages(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark),s=>s.Contains("Engine: fast",StringComparison.Ordinal));
            Assert.DoesNotContain(component.RuntimeMessages(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning),s=>s.Contains("Engine: fast",StringComparison.Ordinal));
        }
        finally {component.RemovedFromDocument(document);SceneCache.Shared.Clear();}
    });

    [RhinoFact] public void FastWorkerCarriesSettingsProgressAndDisclosureWithoutARepeatBytePromise() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+System.Environment.Version.Major+".0-windows"));
        using var pool=new DrawingWorkerPool(new(){Count=1,Directory=directory,IdleTimeout=TimeSpan.FromMinutes(2)});
        var progress=new List<JobProgress>();
        using var batch=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"view","fast",1),[View(true,false)],new(),default,new Sink(progress)).GetAwaiter().GetResult();
        var result=Assert.Single(batch.Views);Assert.Null(result.Error);Assert.Equal(EngineMode.Fast,result.Settings!.Engine);
        Assert.True(result.MultipleThreads);Assert.True(result.FastNativeSources>0);
        Assert.Contains("IncludeHiddenCurves=False",result.Info);Assert.Contains("false-Hidden checking skipped",result.Info);
        Assert.Contains(ViewNotices.For(result),n=>n.Level==NoticeLevel.Remark&&n.Text.Contains("Engine: fast",StringComparison.Ordinal));
        Assert.Contains(progress,p=>p.Step==ViewSteps.Fast);
        // Encoding this one result is lossless. A second threaded solve is
        // deliberately not compared byte-for-byte with it or with a local solve.
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(result));
        Assert.Equal(WorkerCodec.CaptureDrawing(result,true),WorkerCodec.CaptureDrawing(restored,true));
    });

    private sealed class Sink(List<JobProgress> values):IProgress<JobProgress>
    {public void Report(JobProgress value){lock(values)values.Add(value);}}
}
