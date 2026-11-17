using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
public sealed class EngineNativeTests(RhinoHost host)
{
    internal static ViewRequest View(string extra = "", object? source = null)
    {
        var view = ViewRequest.Parse(source ?? "Top");
        return view with { Settings = Settings.Resolve([new("v",view.Name,view.Kind,view.Cut is not null,20,1e-6)],
            [["farclip=none\n" + extra]], DocumentUnits.Metres).Views[0] };
    }
    [RhinoTheory] [InlineData(true)] [InlineData(false)]
    public void AllSourcesUseThreadedHldAndRequestedHidden(bool hidden) => host.Run(() =>
    {
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelAbsoluteTolerance=1e-6;
        using var box = new BoundingBox(new Point3d(-1,-1,-1),new Point3d(1,1,1)).ToBrep(); doc.Objects.AddBrep(box);
        doc.Objects.AddLine(new Point3d(-2,0,-2),new Point3d(2,0,-2));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache);
        var options=new List<NativeHldOptions>();
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",1),[View("hidden="+(hidden?"on":"off"))],
            new(){ObserveHldOptions=options.Add},default,null).GetAwaiter().GetResult();
        var result=Assert.Single(batch.Views); Assert.Null(result.Error); Assert.Equal(1,result.NativeCalls);
        Assert.True(result.MultipleThreads); Assert.All(options,o=>{Assert.True(o.MultipleThreads);Assert.Equal(hidden,o.IncludeHiddenCurves);});
        Assert.Contains("Engine: Rhino HLD; multithreaded; visibility not certified",result.Info);
        Assert.DoesNotContain(result.Diagnostics,d=>d.Contains("ray verifier",StringComparison.OrdinalIgnoreCase));
        if(!hidden) Assert.DoesNotContain(result.Items,p=>p.Class==LineClass.Hidden);
    });
    [RhinoFact] public void LegacyEditsReuseProjectionAndTimingButHiddenInvalidates() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null); using var box=new BoundingBox(Point3d.Origin,new Point3d(2,2,2)).ToBrep(); doc.Objects.AddBrep(box);
        using var sceneCache=new SceneCache(); using var drawings=new DrawingCache();
        var identity=new RequestIdentity("s","v","f",1);
        DrawingBatch Solve(string text)=>drawings.SolveAsync(BaselineTests.Capture(doc,sceneCache),identity,[View(text)],new(),[],default).GetAwaiter().GetResult();
        using var first=Solve(""); var before=NativeDrawing.HldCalls;
        var fingerprint=WorkerCodec.Fingerprint();
        foreach(var text in new[]{"engine=exact","engine=native","engine=fast","joints=off","joints=on"})
        {
            using var edited=Solve(text); Assert.Equal(before,NativeDrawing.HldCalls);
            Assert.Equal(WorkerCodec.TimingKey(View(),new(),fingerprint),WorkerCodec.TimingKey(View(text),new(),fingerprint));
        }
        using var changed=Solve("hidden=off"); Assert.Equal(before+1,NativeDrawing.HldCalls);
        Assert.NotEqual(WorkerCodec.TimingKey(View(),new(),fingerprint),WorkerCodec.TimingKey(View("hidden=off"),new(),fingerprint));
        Assert.False(fingerprint.Accepts(fingerprint with{Schema=3}));
    });
    [RhinoFact] public void ViewBoxDefaultsOmitLegacyKeysButAuthoredTextIsRead() => host.Run(() =>
    {
        Assert.DoesNotContain("engine",M2DPlus.Rhino.Views.ViewBoxes.Keys);
        Assert.DoesNotContain("joints",M2DPlus.Rhino.Views.ViewBoxes.Keys);
        using var attributes=new global::Rhino.DocObjects.ObjectAttributes();
        attributes.SetUserString("m2d.engine","fast");attributes.SetUserString("m2d.joints","off");
        var row=M2DPlus.Rhino.Views.ViewBoxes.ReadRow(attributes);
        Assert.Equal("fast",row.Values["engine"]);Assert.Equal("off",row.Values["joints"]);
    });
    [RhinoFact] public void OldDurableDrawingBlobsMissBeforeNativeRestoration() => host.Run(() =>
    {
        using var drawing=new ViewDrawing();
        var bytes=WorkerCodec.CaptureDrawing(drawing);
        var old=System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();old.Remove("Schema");old.Remove("Fingerprint");
        Assert.Throws<InvalidDataException>(()=>WorkerCodec.RestoreDrawing(System.Text.Encoding.UTF8.GetBytes(old.ToJsonString())));
        var dto=WorkerCodec.Decode<WorkerCodec.Drawing>(bytes);
        Assert.Throws<InvalidDataException>(()=>WorkerCodec.RestoreDrawing(WorkerCodec.Encode(dto with{Fingerprint=dto.Fingerprint with{Schema=3}})));
    });
    [RhinoTheory] [InlineData(false)] [InlineData(true)]
    public void IsolatedAndCurveScenePointsHaveExplicitNativeFailurePolicy(bool curve) => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null); doc.Objects.AddPoint(0,0,0);
        if(curve)doc.Objects.AddLine(new Point3d(-1,-1,0),new Point3d(1,-1,0));
        using var cache=new SceneCache(); using var scene=BaselineTests.Capture(doc,cache);
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",1),[View()],new(),default,null).GetAwaiter().GetResult();
        var drawing=batch.Views[0]; Assert.Null(drawing.Error);Assert.False(drawing.Report.Quarantined);
        Assert.True(drawing.Items.Any(p=>{using var g=p.CopyGeometry();return g is Point;}) ||
            drawing.Diagnostics.Any(d=>d.Contains("native point",StringComparison.Ordinal)));
        using var restored=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(drawing));
        Assert.Equal(WorkerCodec.CaptureDrawing(drawing),WorkerCodec.CaptureDrawing(restored));
    });
    [RhinoFact] public void CutOnlyGlassChangesResolveExcludedForegroundRegionsAgain() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=1e-6;
        using var box=new BoundingBox(new Point3d(-2,-2,-1),new Point3d(2,2,1)).ToBrep();var cutId=doc.Objects.AddBrep(box);
        var layer=doc.Layers.Add("Glass foreground",System.Drawing.Color.LightBlue);
        using var front=new BoundingBox(new Point3d(-1,-1,.2),new Point3d(1,1,1)).ToBrep();
        doc.Objects.AddBrep(front,new global::Rhino.DocObjects.ObjectAttributes{LayerIndex=layer});
        using var scene=new SceneCache();using var cache=new DrawingCache();
        ViewRequest ViewFor(string glass)
        {
            var view=View("cutonly=on\nglass="+glass,Plane.WorldXY);
            return view with{Cut=view.Cut! with{ScopeEnabled=true,Objects=new[]{cutId}}};
        }
        DrawingBatch Draw(string glass)=>cache.SolveAsync(BaselineTests.Capture(doc,scene),new("s","v","f",1),[ViewFor(glass)],new(),[],default).GetAwaiter().GetResult();
        using var opaque=Draw("opaque");Assert.Empty(opaque.Views[0].Regions);
        var before=NativeDrawing.VisibilitySolves;var hld=NativeDrawing.HldCalls;
        using var clear=Draw("clear");Assert.Equal(before+1,NativeDrawing.VisibilitySolves);Assert.Equal(hld,NativeDrawing.HldCalls);
        Assert.Single(clear.Views[0].Regions);Assert.NotNull(clear.Views[0].Regions[0].Region.Hatch);
        Assert.Equal(16,clear.Views[0].Regions[0].Region.Area,8);
        Assert.Equal(0,clear.Views[0].NativeCalls);Assert.Contains("sections only; HLD not run",clear.Views[0].EngineSummary);
    });
    [RhinoTheory] [InlineData(0)] [InlineData(.00005)]
    public void ZeroAndThinEmptySectionsHaveTheirOwnGreyNotice(double depth) => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelAbsoluteTolerance=.0001;
        using var box=new BoundingBox(new Point3d(-1,-1,-3),new Point3d(1,1,-2)).ToBrep();doc.Objects.AddBrep(box);
        using var cache=new SceneCache();using var scene=BaselineTests.Capture(doc,cache);
        var view=View("farclip=noline\ntolerance=0.0001\ndepth="+depth.ToString("R",System.Globalization.CultureInfo.InvariantCulture),Plane.WorldXY);
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new("s","v","f",1),[view],new(),default,null).GetAwaiter().GetResult();
        var drawing=batch.Views[0];Assert.Null(drawing.Error);Assert.Empty(drawing.Regions);Assert.Equal(0,drawing.NativeCalls);
        Assert.DoesNotContain(ViewNotices.CutOnlyEmpty,drawing.Diagnostics);
        var expected=depth==0?ViewNotices.ZeroDepth:ViewNotices.ThinDepth;
        Assert.Contains(ViewNotices.For(drawing),n=>n.Level==NoticeLevel.Remark&&n.Text.Contains(expected,StringComparison.Ordinal));
        Assert.DoesNotContain("multithreaded",drawing.EngineSummary);Assert.True(drawing.MultipleThreads);
    });
    [RhinoTheory] [InlineData("")] [InlineData("joints=default")] [InlineData("joints=off")]
    public void JointsInfoIsOnlyForExplicitLegacyInstructions(string text) => host.Run(() =>
    {
        using var drawing=new ViewDrawing{Settings=View(text).Settings};
        Assert.Equal(text=="joints=off",drawing.Info.Contains("Joints suppression",StringComparison.Ordinal));
        Assert.DoesNotContain("multithreaded",drawing.EngineSummary);Assert.True(drawing.MultipleThreads);
    });
    [RhinoFact] public void ComputeFingerprintIsLazilySharedAcrossViews() => host.Run(() =>
    {
        Assert.Same(WorkerCodec.Fingerprint(),WorkerCodec.Fingerprint());
    });
}
