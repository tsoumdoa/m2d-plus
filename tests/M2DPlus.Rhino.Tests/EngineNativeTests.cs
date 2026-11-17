using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class EngineNativeTests(RhinoHost host)
{
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc doc,SceneCache cache)
    {
        using var watch=new DocumentWatch(doc);using var capture=new SceneCapture(doc,watch,null,cache);
        while(!capture.Step(TimeSpan.FromSeconds(30))){}return capture.Accept();
    }
    private static ViewRequest View(bool native,bool hidden,bool cut=false,double minVisible=0,bool forSolve=true)
    {
        var view=cut?ViewRequest.Parse(new Plane(Point3d.Origin,Vector3d.ZAxis)):ViewRequest.Parse("Back");
        var setting=Settings.Resolve([new("v",view.Name,view.Kind,cut,20,1e-6)],
            [[$"engine={(native?"native":"exact")}\nhidden={(hidden?"on":"off")}\nmin visible={minVisible}"]],DocumentUnits.Metres).Views[0];
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
    public void NativeCallsFollowEngineHiddenAndRoutePolicy(bool native,bool hidden,bool nativeOnly) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        // A bare freeform patch takes the ordinary native fallback. Adding a
        // line gives composition an exact candidate and exercises mixed routing.
        if(!nativeOnly)doc.Objects.AddLine(new Point3d(-2,-2,-.5),new Point3d(2,2,-.5));
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var options=new List<NativeHldOptions>();var reports=new List<JobProgress>();
        using var result=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","settings",1),[View(native,hidden)],
            new(){ObserveHldOptions=options.Add},default,new Sink(reports)).GetAwaiter().GetResult();
        var drawing=Assert.Single(result.Views);Assert.Null(drawing.Error);Assert.NotEmpty(options);
        Assert.All(options,o=>{Assert.Equal(native,o.MultipleThreads);Assert.Equal(!native||hidden,o.IncludeHiddenCurves);});
        if (native) Assert.DoesNotContain(drawing.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
        else Assert.Contains(drawing.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
        if(native)
        {
            Assert.True(drawing.FastNativeSources>0);Assert.True(drawing.MultipleThreads);
            Assert.Contains("native: Rhino HiddenLineDrawing, not certified",drawing.Info);
            var notice=Assert.Single(ViewNotices.For(drawing),n=>n.Text.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
            Assert.Equal(NoticeLevel.Remark,notice.Level);
            var component=System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"M2DPlus.Grasshopper.gha")).GetType("M2DPlus.Grasshopper.DrawingComponent",true)!;
            var level=component.GetMethod("Level",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark,level.Invoke(null,[notice.Level]));
            Assert.Contains(reports,p=>p.Step==ViewSteps.Native);
            Assert.Equal(0,drawing.Report.UncertainCount);Assert.DoesNotContain(drawing.Items,p=>p.Class==LineClass.Unresolved);
            using var roundTrip=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
            Assert.Equal(drawing.FastNativeSources,roundTrip.FastNativeSources);Assert.Equal(drawing.EngineSummary,roundTrip.EngineSummary);
        }
        else
        {
            Assert.Equal(0,drawing.FastNativeSources);Assert.DoesNotContain(ViewNotices.For(drawing),n=>n.Text.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
            Assert.DoesNotContain(reports,p=>p.Step==ViewSteps.Native);
        }
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void NativeSectionUsesHldClippingOptions(bool hidden) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var sphere=new Sphere(new Point3d(0,0,-.25),1).ToBrep();doc.Objects.AddBrep(sphere);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);var observed=new List<NativeHldOptions>();
        using var result=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","settings",1),[View(true,hidden,cut:true)],
            new(){ObserveHldOptions=observed.Add},default,null).GetAwaiter().GetResult();
        var drawing=Assert.Single(result.Views);Assert.Null(drawing.Error);Assert.NotEmpty(observed);
        Assert.All(observed,o=>{Assert.True(o.MultipleThreads);Assert.Equal(hidden,o.IncludeHiddenCurves);});
        Assert.True(drawing.FastNativeSources>0);Assert.Contains("native: Rhino HiddenLineDrawing",drawing.Info);
        Assert.DoesNotContain(drawing.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
        Assert.NotEmpty(drawing.Regions);Assert.All(drawing.Regions,r=>Assert.Null(r.Region.Hatch));
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void NativeSectionThroughABoxGivesCutLinesAndNullHatches(bool cutOnly) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var box=new BoundingBox(new Point3d(-1,-1,-2),new Point3d(1,1,2)).ToBrep();doc.Objects.AddBrep(box);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var view=ViewRequest.Parse(Plane.WorldXY);
        view=view with{Settings=Settings.Resolve([new("cut","cut",ViewSourceKind.Plane,true,10,1e-6)],
            [[$"engine=native\ncutonly={(cutOnly?"on":"off")}\nfarclip=none\nbeyond=off"]],DocumentUnits.Metres).Views[0]};
        using var raw=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","native",1),[view],new(),default,null).GetAwaiter().GetResult();
        using var displayed=DrawingPresentation.Apply(raw,raw.Identity,[view],[]);
        var drawing=Assert.Single(displayed.Views);Assert.Null(drawing.Error);Assert.Equal(1,drawing.NativeCalls);
        var region=Assert.Single(drawing.Regions).Region;Assert.Null(region.Hatch);
        Assert.Equal(8,region.Loops.Sum(l=>l.Curve.GetLength()),5);
        Assert.Equal(0,drawing.Report.UncertainCount);Assert.DoesNotContain(drawing.Items,p=>p.Class==LineClass.Unresolved);
        Assert.Contains("section cut lines only",drawing.Info);
        if(cutOnly)Assert.Empty(drawing.Items);
        var original=Assert.Single(raw.Views[0].Regions);
        var source=original.Source;
        // A changed boundary still styles cached native lines. An invalid fill scale is irrelevant without hatches
        // and must never invoke certified restyling or create Unresolved.
        var changed=source with {Style=source.Style with {Scale=0,Boundary=source.Style.Boundary with {Colour=0xff123456}}};
        using var restyled=DrawingPresentation.ApplySnapshot(raw,raw.Identity,[view],[],new Dictionary<string,SourceSection>{{original.Region.SourceIds[0],changed}});
        Assert.Equal(0xff123456u,Assert.Single(restyled.Views[0].Regions).Region.Style.Boundary.Colour);
        Assert.Null(restyled.Views[0].Error);Assert.Equal(0,restyled.Views[0].Report.UncertainCount);
    });

    [RhinoFact] public void NativeDepthCropAndSelectiveClippingDisclosure() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        var clipped=doc.Objects.AddLine(new Point3d(-1,0,-1),new Point3d(1,0,1));
        doc.Objects.AddLine(new Point3d(-.2,.2,1),new Point3d(.2,.2,1)); // Requested to remain uncut by the participation list.
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var frame=new Rectangle3d(Plane.WorldXY,new Interval(-.25,.25),new Interval(-.25,.25));
        var view=ViewRequest.Parse(frame) with {Cut=new(Plane.WorldXY,frame,.5,true,false,[clipped],[],null)};
        view=view with {Settings=Settings.Resolve([new("clip","clip",ViewSourceKind.Rectangle,true,10,1e-6)],
            [["engine=native","depth=.5","farclip=noline","hidden=off","beyond=off"]],DocumentUnits.Metres).Views[0]};
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","native",1),[view],new(),default,null).GetAwaiter().GetResult();
        var drawing=Assert.Single(batch.Views);Assert.Null(drawing.Error);Assert.Equal(1,drawing.NativeCalls);
        var id=scene.Value.Prepare(default).Single(s=>s.Section.ObjectIds.Contains(clipped)).Identity;
        var retained=drawing.Items.Where(p=>p.SourceIdentity==id).Sum(p=>{using var g=p.CopyGeometry();return ((Curve)g).GetLength();});
        Assert.Equal(.25,retained,5); // Near, far and crop limits apply to the participating curve.
        Assert.Contains("clipping participation is delegated to Rhino HLD",drawing.Info);
        Assert.Contains("excluded sources may still be clipped",drawing.Info);
        // Rhino 8.36 omits the excluded foreground curve even with explicit per-object plane lists. Do not correct
        // its visibility with our engines or assert that native mode certifies it; a future native fix is welcome.
        Assert.All(drawing.Items,p=>Assert.Equal(LineClass.Visible,p.Class));
        Assert.Equal(0,drawing.Report.UncertainCount);
    });

    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void PlanarBoxUsesHldInNativeWhileExactKeepsTheKernel(bool hidden) => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var a=new BoundingBox(new Point3d(-2,-1,-1),new Point3d(2,1,1)).ToBrep();doc.Objects.AddBrep(a);
        using var b=new BoundingBox(new Point3d(-1,2,-.5),new Point3d(1,3,.5)).ToBrep();doc.Objects.AddBrep(b);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var ev=View(false,hidden,minVisible:.1,forSolve:false);var fv=View(true,hidden,minVisible:.1,forSolve:false);
        using var exact=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","e",1),[ev with{Settings=ev.Settings!.ForVisibilitySolve()}],new(),default,null).GetAwaiter().GetResult();
        using var native=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",2),[fv with{Settings=fv.Settings!.ForVisibilitySolve()}],new(),default,null).GetAwaiter().GetResult();
        using var exactDisplay=DrawingPresentation.Apply(exact,exact.Identity,[ev],[]);
        using var nativeDisplay=DrawingPresentation.Apply(native,native.Identity,[fv],[]);
        var x=exactDisplay.Views[0];var y=nativeDisplay.Views[0];Assert.Null(x.Error);Assert.Null(y.Error);
        Assert.Equal(0,x.NativeCalls);Assert.Equal(1,y.NativeCalls);Assert.True(y.FastNativeSources>0);Assert.True(y.MultipleThreads);
        Assert.Equal(0,y.KernelEdges);Assert.NotEmpty(y.Items);Assert.Equal(0,y.Report.UncertainCount);
        Assert.DoesNotContain(y.Items,p=>p.Class==LineClass.Unresolved);
        Assert.DoesNotContain(y.Diagnostics,d=>d.StartsWith("Native ray verifier:",StringComparison.Ordinal));
        Assert.Contains(ViewNotices.For(y),n=>n.Level==NoticeLevel.Remark&&n.Text.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
        Assert.Equal(0,x.Report.UncertainCount);Assert.NotEmpty(x.Items);
        Assert.Equal(hidden ? .1 : 0,y.MinVisible);
        if(!hidden)Assert.Contains("min visible ignored with hidden = off",y.Info);
    });

    [RhinoTheory] [InlineData("native")] [InlineData("fast")]
    public void ViewObjectEngineUserTextUsesTheSamePrecedence(string engine) => host.Run(()=>
    {
        var attributes=new ObjectAttributes();attributes.SetUserString("m2d.engine",engine);
        var row=ViewBoxes.ReadRow(attributes);Assert.Equal(engine,row.Values["engine"]);
        var context=new ViewSettingsContext("v","v",ViewSourceKind.Rectangle,false,20,.001);
        var menu=new EditorSettings(new([KeyValuePair.Create("engine","exact")]),[KeyValuePair.Create("v",row)]);
        Assert.Equal(EngineMode.Native,Settings.Resolve([context],[],DocumentUnits.Metres,menu).Views[0].Engine);
        Assert.Equal(EngineMode.Exact,Settings.Resolve([context],[["engine=exact"]],DocumentUnits.Metres,menu).Views[0].Engine);
    });

    [RhinoFact] public void DrawingCacheSeparatesEngineAndNativeHiddenButReusesExactHidden() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        using var scenes=new SceneCache();using var drawings=new DrawingCache();
        DrawingBatch Draw(bool native,bool hidden)
        {
            var lease=Capture(doc,scenes);
            var view=View(native,hidden);
            // The presentation request must retain hidden=off; only the cache
            // normalizes the settings passed into the visibility solve.
            var context=view.Settings!.View;
            view=view with{Settings=Settings.Resolve([context],[[$"engine={(native?"native":"exact")}\nhidden={(hidden?"on":"off")}"]],DocumentUnits.Metres).Views[0]};
            return drawings.SolveAsync(lease,new(lease.Value.Revision,"view","settings",1),[view],new(),[],default).GetAwaiter().GetResult();
        }
        using var exactOn=Draw(false,true);var afterExact=NativeDrawing.HldCalls;
        using var exactOff=Draw(false,false);Assert.Equal(afterExact,NativeDrawing.HldCalls);
        using var nativeOff=Draw(true,false);Assert.Equal(afterExact+1,NativeDrawing.HldCalls);Assert.True(nativeOff.Views[0].FastNativeSources>0);
        Assert.Contains("IncludeHiddenCurves=False",nativeOff.Views[0].Info);
        using var repeat=Draw(true,false);Assert.Equal(afterExact+1,NativeDrawing.HldCalls);
        using var nativeOn=Draw(true,true);Assert.Equal(afterExact+2,NativeDrawing.HldCalls);
        Assert.Contains("IncludeHiddenCurves=True",nativeOn.Views[0].Info);
    });

    [RhinoFact] public void ActualComponentEmitsNativeInfoAndARemark() => host.Run(()=>
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
            Assert.Contains("native: Rhino HiddenLineDrawing, not certified",info);
            Assert.Contains(component.RuntimeMessages(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark),s=>s.Contains("engine = fast is now engine = native",StringComparison.Ordinal));
            Assert.Equal(0,component.Params.Output[6].VolatileDataCount);
            Assert.Contains(component.RuntimeMessages(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark),s=>s.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
            Assert.DoesNotContain(component.RuntimeMessages(global::Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning),s=>s.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
        }
        finally {component.RemovedFromDocument(document);SceneCache.Shared.Clear();}
    });

    [RhinoFact] public void NativeWorkerCarriesSettingsProgressAndDisclosureWithoutARepeatBytePromise() => host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var body=Freeform();doc.Objects.AddBrep(body);
        using var cache=new SceneCache();using var scene=Capture(doc,cache);
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+System.Environment.Version.Major+".0-windows"));
        using var pool=new DrawingWorkerPool(new(){Count=1,Directory=directory,IdleTimeout=TimeSpan.FromMinutes(2)});
        var progress=new List<JobProgress>();
        using var batch=pool.SolveAsync(scene.Value,new(scene.Value.Revision,"view","native",1),[View(true,false)],new(),default,new Sink(progress)).GetAwaiter().GetResult();
        var result=Assert.Single(batch.Views);Assert.Null(result.Error);Assert.Equal(EngineMode.Native,result.Settings!.Engine);
        Assert.True(result.MultipleThreads);Assert.True(result.FastNativeSources>0);
        Assert.Contains("IncludeHiddenCurves=False",result.Info);Assert.Equal(0,result.Report.UncertainCount);
        Assert.Contains(ViewNotices.For(result),n=>n.Level==NoticeLevel.Remark&&n.Text.Contains("native: Rhino HiddenLineDrawing",StringComparison.Ordinal));
        Assert.Contains(progress,p=>p.Step==ViewSteps.Native);
        // Encoding this one result is lossless. A second threaded solve is
        // deliberately not compared byte-for-byte with it or with a local solve.
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(result));
        Assert.Equal(WorkerCodec.CaptureDrawing(result,true),WorkerCodec.CaptureDrawing(restored,true));
    });

    private sealed class Sink(List<JobProgress> values):IProgress<JobProgress>
    {public void Report(JobProgress value){lock(values)values.Add(value);}}
}
