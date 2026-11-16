using System.Collections.Concurrent;
using M2DPlus.Core;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category","RhinoIntegration")]
public sealed class UnitScaleRhinoTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoFact]
    public void ElevationTopologyAndClassesAreInvariantInMillimetresAtSurveyCoordinates() => host.Run(() =>
    {
        (string Source,LineClass Class,Point3d A,Point3d B)[][]? expected=null;
        foreach(var (scale,offset) in new[]{(1d,0d),(1000d,0d),(1000d,1000000d),(1000d,10000000d)})
        {
            using var doc=RhinoDoc.CreateHeadless(null);doc.ModelUnitSystem=scale==1?UnitSystem.Meters:UnitSystem.Millimeters;
            doc.ModelAbsoluteTolerance=.00001*scale;
            Point3d V(double x,double y,double z)=>new(x*scale+offset,y*scale+offset,z*scale+offset);
            using var box=Brep.CreateFromBox(new BoundingBox(V(0,0,0),V(2,2,3)));
            using var panel=Brep.CreateFromBox(new BoundingBox(V(1,3,1),V(3,4,2)));
            using var line=new LineCurve(V(-1,1,1),V(4,1,1));
            CaptureInput[] inputs=[new(box,[],"box"),new(panel,[],"panel"),new(line,[],"line")];
            using var cache=new SceneCache();using var scene=BaselineTests.Capture(doc,cache,inputs);
            var views=new[]{"Back","Front","Right","Left"}.Select(v=>ViewRequest.Parse(v)).ToArray();
            using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new(scene.Value.Revision,"views","settings",1),views,new(),default,null).GetAwaiter().GetResult();
            var actual=batch.Views.Select((view,v)=>
            {
                Assert.Null(view.Error);Assert.Equal(0,view.NativeCalls);
                var pieces=view.Items.Select(piece=>{
                    using var geometry=piece.CopyGeometry();var curve=Assert.IsAssignableFrom<Curve>(geometry);
                    Point3d Normalize(Point3d p)=>new((p.X-offset)/scale,(p.Y-offset)/scale,(p.Z-offset)/scale);
                    return(piece.SourceIdentity,piece.Class,Normalize(curve.PointAtStart),Normalize(curve.PointAtEnd));
                }).ToArray();
                // Compare the drawing topology after joining collinear same-class,
                // same-source intervals. Filtered endpoint rounding may leave an
                // extra partition boundary without changing any drawing locus.
                var plane=new Plane(Point3d.Origin,-views[v].Direction);
                return pieces.GroupBy(p=>(p.SourceIdentity,p.Class)).SelectMany(group=>
                {
                    var loci=group.Select(p=>{plane.ClosestParameter(p.Item3,out var ax,out var ay);plane.ClosestParameter(p.Item4,out var bx,out var by);
                        return new Locus(ax,ay,bx,by,VisibilityClass.Visible,p.SourceIdentity);}).ToArray();
                    return VisibilityLoci.Union(loci,.00001).Select(l=>(group.Key.SourceIdentity,group.Key.Class,plane.PointAt(l.X0,l.Y0),plane.PointAt(l.X1,l.Y1)));
                }).ToArray();
            }).ToArray();
            if(expected is null){expected=actual;continue;}
            for(int v=0;v<actual.Length;v++)
            {
                var extra=actual[v].Where(p=>!expected[v].Any(piece=>Same(p,piece))).ToArray();
                Assert.True(expected[v].Length==actual[v].Length,$"scale={scale} offset={offset} view={views[v].Name}; expected={expected[v].Length} actual={actual[v].Length}; extra={string.Join(";",extra)}");
                // Geometry sorting strings need not sort the same way after scaling.
                foreach(var piece in expected[v]) Assert.Contains(actual[v],p=>Same(p,piece));
            }
        }
    });

    private static bool Same((string Source,LineClass Class,Point3d A,Point3d B) a,(string Source,LineClass Class,Point3d A,Point3d B) b) =>
        a.Source==b.Source&&a.Class==b.Class&&(a.A.DistanceTo(b.A)<1e-7&&a.B.DistanceTo(b.B)<1e-7||a.A.DistanceTo(b.B)<1e-7&&a.B.DistanceTo(b.A)<1e-7);

    [RhinoFact]
    public void NativeCameraDistancesScaleWithTheSceneAndTolerance() => host.Run(() =>
    {
        Point3d expectedCamera=default;double[]? expectedFrustum=null;
        foreach(double scale in new[]{1d,1000d})
        {
            var bounds=new BoundingBox(Point3d.Origin,new Point3d(2*scale,3*scale,4*scale));
            var frame=new Rectangle3d(new Plane(new Point3d(0,0,10*scale),Vector3d.ZAxis),new Interval(-3*scale,3*scale),new Interval(-4*scale,4*scale));
            using var viewport=new global::Rhino.DocObjects.ViewportInfo();
            NativeViewport.Configure(viewport,frame,-Vector3d.ZAxis,bounds,.00001*scale);
            Assert.True(viewport.GetFrustum(out var left,out var right,out var bottom,out var top,out var near,out var far));
            var camera=viewport.CameraLocation/scale;
            var frustum=new[]{left,right,bottom,top,near,far}.Select(x=>x/scale).ToArray();
            if(expectedFrustum is null){expectedCamera=camera;expectedFrustum=frustum;}
            else {Assert.True(camera.DistanceTo(expectedCamera)<1e-10);for(int i=0;i<frustum.Length;i++)Assert.Equal(expectedFrustum[i],frustum[i],10);}
        }
    });

    [RhinoFact]
    public void LocalAndWorkerReportPhaseProgressWithoutChangingDrawingBytes() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);using var cache=new SceneCache();
        // An ellipsoid: a sphere is an exact analytic solid and would report "exact curved", not "hidden lines".
        using var ellipsoid=NurbsSurface.CreateFromSphere(new Sphere(Point3d.Origin,1));Assert.True(ellipsoid.Transform(Transform.Scale(Plane.WorldXY,1,1,1.5)));
        using var sphere=ellipsoid.ToBrep();doc.Objects.AddBrep(sphere);
        using var scene=BaselineTests.Capture(doc,cache);var identity=new RequestIdentity(scene.Value.Revision,"views","settings",1);
        ViewRequest[] views=[ViewRequest.Parse("Front")];
        var localProgress=new Sink();
        using var local=NativeDrawing.SolveBorrowedAsync(scene.Value,identity,views,new(),default,localProgress).GetAwaiter().GetResult();
        // Inner phases feed the one status model: the view's step with that step's own percentage.
        Assert.Contains(localProgress.Values,p=>p.Phase==JobPhase.Drawing && p.View==views[0].Name && p.Step==ViewSteps.HiddenLines && p.StepFraction is not null);
        var workerProgress=new Sink();
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+System.Environment.Version.Major+".0-windows"));
        using var pool=new DrawingWorkerPool(new(){Count=1,Directory=directory});
        using var worker=pool.SolveAsync(scene.Value,identity,views,new(),default,workerProgress).GetAwaiter().GetResult();
        Assert.Null(worker.Views[0].Error);
        // The child's steps cross the pipe and come back with the view they belong to.
        Assert.Contains(workerProgress.Values,p=>p.Phase==JobPhase.Drawing && p.Workers==1 && p.View==views[0].Name && p.Step==ViewSteps.HiddenLines && p.StepFraction is not null);
        Assert.Equal(WorkerCodec.CaptureDrawing(local.Views[0],true),WorkerCodec.CaptureDrawing(worker.Views[0],true));
    });
    private sealed class Sink:IProgress<JobProgress>
    {
        internal ConcurrentQueue<JobProgress> Values {get;}=new();
        public void Report(JobProgress value)=>Values.Enqueue(value);
    }

    [RhinoFact]
    public void SingleViewNativeCancellationReleasesTheSlotPromptly() => host.Run(() =>
    {
        using var doc=RhinoDoc.CreateHeadless(null);using var cache=new SceneCache();
        doc.ModelUnitSystem=UnitSystem.Millimeters;doc.ModelAbsoluteTolerance=.01;
        for(int i=0;i<50;i++)
        { using var sphere=new Sphere(new Point3d(i%10*1500,i/10*1500,0),1000).ToBrep();doc.Objects.AddBrep(sphere); }
        using var scene=BaselineTests.Capture(doc,cache);using var cancel=new CancellationTokenSource();
        var sink=new CancelSink(cancel);NativeDrawing.NativeProgress=sink;
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(()=>NativeDrawing.SolveBorrowedAsync(scene.Value,
                new(scene.Value.Revision,"view","settings",1),[ViewRequest.Parse("Front")],new(NativeOnly:true),cancel.Token,null).GetAwaiter().GetResult());
            Assert.True(sink.CancelledAt!=0,"Native progress callback was not entered");
            var latency=System.Diagnostics.Stopwatch.GetElapsedTime(sink.CancelledAt);
            output.WriteLine($"Single-view native cancellation returned in {latency.TotalMilliseconds:F1} ms after the callback cancelled.");
            Assert.True(latency<TimeSpan.FromSeconds(2),$"Native cancel latency: {latency}");
            Assert.True(NativeScheduler.Global.RunAsync(1,_=>true,default).GetAwaiter().GetResult());
        }
        finally { NativeDrawing.NativeProgress=null; }
    });
    private sealed class CancelSink(CancellationTokenSource cancel):IProgress<double>
    {
        internal long CancelledAt;
        public void Report(double value)
        {
            if(Interlocked.CompareExchange(ref CancelledAt,System.Diagnostics.Stopwatch.GetTimestamp(),0)==0)cancel.Cancel();
        }
    }
}
