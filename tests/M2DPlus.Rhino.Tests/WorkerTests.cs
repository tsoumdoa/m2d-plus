using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category","RhinoIntegration")]
public sealed class WorkerTests(RhinoHost host)
{
    private static WorkerOptions Options(int count=1,string? fault=null) => new()
    {
        Count=count,Directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/M2DPlus.Worker/bin/Debug/net"+Environment.Version.Major+".0-windows")),
        TestFault=fault,IdleTimeout=TimeSpan.FromMinutes(2)
    };
    private static LeaseEntry<PreparedScene>.Lease Capture(RhinoDoc doc,SceneCache cache)
    {
        using var watch=new DocumentWatch(doc);using var capture=new SceneCapture(doc,watch,null,cache);
        while(!capture.Step(TimeSpan.FromSeconds(30))){}return capture.Accept();
    }
    private static void Populate(RhinoDoc doc)
    {
        using var box=Brep.CreateFromBox(new BoundingBox(Point3d.Origin,new Point3d(2,2,2)));doc.Objects.AddBrep(box);
        doc.Objects.AddLine(new Point3d(-1,1,1),new Point3d(3,1,1));doc.Objects.AddPoint(1,1,-1);
        using var sphere=new Sphere(new Point3d(4,0,0),1).ToBrep();doc.Objects.AddBrep(sphere);
        var definition=doc.InstanceDefinitions.Add("worker-block","",Point3d.Origin,[box],[new global::Rhino.DocObjects.ObjectAttributes()]);
        doc.Objects.AddInstanceObject(definition,Transform.Translation(10,0,0)*Transform.Mirror(Plane.WorldYZ));
    }
    private static RequestIdentity Identity(PreparedScene scene,long generation=1)=>new(scene.Revision,"views","settings",generation);
    private static ViewRequest[] Views
    {
        get
        {
            ViewRequest[] views=[ViewRequest.Parse("Top"),ViewRequest.Parse("Front"),ViewRequest.Parse(new Plane(new Point3d(0,0,1),Vector3d.ZAxis))];
            var settings=Settings.Resolve(views.Select((v,i)=>new ViewSettingsContext(i.ToString(),v.Name,v.Kind,v.Cut is not null,30,.001)).ToArray(),
                new[]{new[]{"depth = 20 m","hidden = on","tangents = on","scale = 1:50","hatch = black","gap = 5 m"}},DocumentUnits.Metres);
            return views.Select((v,i)=>v with{Settings=settings.Views[i]}).ToArray();
        }
    }
    [RhinoFact] public void PersistentParallelWorkersAreByteIdenticalAndDetachAllResults()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var baseline=NativeDrawing.SolveBorrowedAsync(lease.Value,Identity(lease.Value),Views,new(),default,null).GetAwaiter().GetResult();
        using var pool=new DrawingWorkerPool(Options(2));
        using var first=pool.SolveAsync(lease.Value,Identity(lease.Value),Views,new(),default).GetAwaiter().GetResult();
        var pids=pool.ProcessIds;
        using var second=pool.SolveAsync(lease.Value,Identity(lease.Value,2),Views,new(),default).GetAwaiter().GetResult();
        Assert.Equal(pids,pool.ProcessIds);Assert.Equal(2,pids.Length);
        for(var i=0;i<Views.Length;i++)
        {
            Assert.Null(first.Views[i].Error);Assert.Null(second.Views[i].Error);
            Assert.Equal(WorkerCodec.Hash(WorkerCodec.CaptureDrawing(baseline.Views[i],true)),WorkerCodec.Hash(WorkerCodec.CaptureDrawing(first.Views[i],true)));
            Assert.Equal(WorkerCodec.CaptureDrawing(first.Views[i],true),WorkerCodec.CaptureDrawing(second.Views[i],true));
        }
    });
    [RhinoFact] public void RuntimeMismatchRefusedBeforeScene()=>host.Run(()=>
    {
        var fingerprint=WorkerCodec.Fingerprint() with{RuntimeMajor=Environment.Version.Major==8?10:8};
        Assert.Throws<WorkerUnavailableException>(()=>WorkerClient.StartAsync(Options(),fingerprint,default).GetAwaiter().GetResult());
    });
    [RhinoFact] public void CrashFailsOnlyItsShardAndNextRequestRestarts()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var pool=new DrawingWorkerPool(Options(2));var killed=0;
        pool.SolvingView=(pid,_)=>{if(Interlocked.Exchange(ref killed,1)!=0)return;using var p=System.Diagnostics.Process.GetProcessById(pid);p.Kill(true);};
        using var result=pool.SolveAsync(lease.Value,Identity(lease.Value),Views,new(NativeOnly:true),default).GetAwaiter().GetResult();
        Assert.True(result.Complete);Assert.Contains(result.Views,v=>v.Error is not null);Assert.Contains(result.Views,v=>v.Error is null);
        Assert.All(result.Views.Where(v=>v.Error is not null),v=>{Assert.True(v.Report.Quarantined);Assert.Contains("Worker failed",v.Info);});
        pool.SolvingView=null;
        using var recovery=pool.SolveAsync(lease.Value,Identity(lease.Value,2),Views,new(NativeOnly:true),default).GetAwaiter().GetResult();
        Assert.All(recovery.Views,v=>Assert.Null(v.Error));
    });
    [RhinoFact] public void CancellationHardKillsBlockedNativeAndDocumentCloseRejectsPublication()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var pool=new DrawingWorkerPool(Options(fault:"block"));using var gate=new RequestGate();var request=gate.Begin(lease.Value.Revision,"views","settings");
        using var started=new ManualResetEventSlim();var pid=0;pool.SolvingView=(id,_)=>{pid=id;started.Set();};
        var task=pool.SolveAsync(lease.Value,request.Identity,Views,new(NativeOnly:true),request.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)),"Worker never entered native solve");
        // Document lifetime is controlled on the host thread, while the contained compute is blocked.
        doc.Dispose();gate.Dispose();
        Assert.ThrowsAny<OperationCanceledException>(()=>task.GetAwaiter().GetResult());
        Assert.False(gate.TryPublish(request.Identity,true,()=>throw new Exception("stale publication")));
        Assert.True(pid>0);Assert.Throws<ArgumentException>(()=>System.Diagnostics.Process.GetProcessById(pid));Assert.Empty(pool.ProcessIds);
    });
    [RhinoFact] public void StaleReplyIsRejectedAndMemoryCapIsEnforced()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using(var pool=new DrawingWorkerPool(Options()))
        {
            pool.ObserveReply=r=>r with{Identity=r.Identity with{Generation=r.Identity.Generation-1}};
            using var result=pool.SolveAsync(lease.Value,Identity(lease.Value),[Views[0]],new(),default).GetAwaiter().GetResult();
            Assert.Contains("Stale worker reply rejected",result.Views[0].Error);Assert.Empty(result.Views[0].Items);
        }
        using(var pool=new DrawingWorkerPool(Options(fault:"memory") with{MemoryBytes=1024L*1024*1024}))
        {
            using var result=pool.SolveAsync(lease.Value,Identity(lease.Value),[Views[0]],new(NativeOnly:true),default).GetAwaiter().GetResult();
            Assert.NotNull(result.Views[0].Error);Assert.DoesNotContain("Memory cap failed",result.Views[0].Error);
        }
    });
    [RhinoFact] public void AdmissionKeepsSingleViewLocalRoutesSetsAndExpiresIdleWorkers()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var pool=new DrawingWorkerPool(Options() with{IdleTimeout=TimeSpan.FromMilliseconds(300)});
        using(var small=DrawingExecution.SolveBorrowedAsync(lease.Value,Identity(lease.Value),[Views[0]],new(),default,null,pool).GetAwaiter().GetResult())
        {Assert.Null(small.Views[0].Error);Assert.Empty(pool.ProcessIds);}
        using(var set=DrawingExecution.SolveBorrowedAsync(lease.Value,Identity(lease.Value),Views,new(),default,null,pool).GetAwaiter().GetResult())
        {Assert.All(set.Views,v=>{Assert.Null(v.Error);Assert.Contains(v.Diagnostics,d=>d.StartsWith("Worker:"));});}
        Assert.True(SpinWait.SpinUntil(()=>pool.ProcessIds.Length==0,TimeSpan.FromSeconds(5)),"Idle process did not expire");
        using(var heavy=DrawingExecution.SolveBorrowedAsync(lease.Value,Identity(lease.Value),[Views[0]],new(){Scheduler=new(1)},default,null,pool).GetAwaiter().GetResult())
        {Assert.Null(heavy.Views[0].Error);Assert.Contains(heavy.Views[0].Diagnostics,d=>d.StartsWith("Worker:"));}
        using var absent=new DrawingWorkerPool(Options() with{Directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"))});
        var failure=Assert.Throws<InvalidOperationException>(()=>DrawingExecution.SolveBorrowedAsync(lease.Value,Identity(lease.Value),Views,new(){Scheduler=new(1)},default,null,absent).GetAwaiter().GetResult());
        Assert.Contains(SceneCost.Refusal,failure.Message);
    });

    [RhinoFact] public void MemoryFailureRetriesOnlyThatViewOnceAloneWithLargerCap()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var pool=new DrawingWorkerPool(Options(2,"memory-once") with{MemoryBytes=WorkerMemoryPolicy.GiB,ReadMemory=()=>new(16*WorkerMemoryPolicy.GiB,12*WorkerMemoryPolicy.GiB)});
        var initial=new List<int>();var retries=new List<int>();
        pool.AttemptingView=(index,cap,retry)=>
        {
            lock(initial)
            {
                if(retry){Assert.Single(pool.ProcessIds);Assert.Equal(2*WorkerMemoryPolicy.GiB,cap);retries.Add(index);}
                else initial.Add(index);
            }
        };
        // One native view is forced over its cap; a view with an authored error needs no native allocation.
        using var batch=pool.SolveAsync(lease.Value,Identity(lease.Value),[Views[0],new("invalid",default,"authored view error")],new(NativeOnly:true),default).GetAwaiter().GetResult();
        Assert.Equal(new[]{0},retries);Assert.Equal(2,initial.Count);
        Assert.Null(batch.Views[0].Error);Assert.Contains(batch.Views[0].Diagnostics,d=>d.Contains("retried once alone"));
        Assert.Equal("authored view error",batch.Views[1].Error);
    });
    [RhinoFact] public void NoRetryHeadroomFailsOnlyAffectedViewWithActionableInfo()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);Populate(doc);using var cache=new SceneCache();using var lease=Capture(doc,cache);
        using var pool=new DrawingWorkerPool(Options(2,"memory") with{MemoryBytes=WorkerMemoryPolicy.GiB,ReadMemory=()=>new(4*WorkerMemoryPolicy.GiB,2*WorkerMemoryPolicy.GiB)});
        var retryCount=0;pool.AttemptingView=(_,_,retry)=>{if(retry)retryCount++;};
        using var batch=pool.SolveAsync(lease.Value,Identity(lease.Value),[Views[0],new("invalid",default,"authored view error")],new(NativeOnly:true),default).GetAwaiter().GetResult();
        Assert.Equal(0,retryCount);Assert.Contains("No larger isolated cap",batch.Views[0].Info);Assert.Contains("Close other applications",batch.Views[0].Info);
        Assert.Equal("authored view error",batch.Views[1].Error);
    });
    [RhinoFact] public void WorkerPresentationMatchesHiddenOffMinVisibleAndSection()=>host.Run(()=>
    {
        using var doc=RhinoDoc.CreateHeadless(null);doc.ModelUnitSystem=UnitSystem.Millimeters;doc.ModelAbsoluteTolerance=.001;
        void Box(Point3d a,Point3d b){using var box=Brep.CreateFromBox(new BoundingBox(a,b));doc.Objects.AddBrep(box);}
        Box(new(-1000,0,0),new(0,100,3000));Box(new(3,0,0),new(1000,100,3000));Box(new(-200,500,500),new(200,800,2500));
        using var sceneCache=new SceneCache();using var scene=Capture(doc,sceneCache);var identity=Identity(scene.Value);
        ViewRequest[] views=[ViewRequest.Parse("Front"),ViewRequest.Parse("Front"),ViewRequest.Parse(new Plane(new Point3d(0,0,1500),Vector3d.ZAxis))];
        var settings=Settings.Resolve(views.Select((v,i)=>new ViewSettingsContext(i.ToString(),v.Name,v.Kind,v.Cut is not null,10000,.001)).ToArray(),
            [["hidden=off","min visible=5 mm","beyond=off","farclip=none"]],DocumentUnits.Millimetres);
        views=views.Select((v,i)=>v with{Settings=settings.Views[i]}).ToArray();
        // Independently cover hidden off without the min-visible filter as well as their combination.
        views[1]=views[1] with{Settings=Settings.Resolve([new("hidden-only","Front",ViewSourceKind.Direction,false,10000,.001)],
            [["hidden=off","min visible=0","beyond=off","farclip=none"]],DocumentUnits.Millimetres).Views[0]};
        using var pool=new DrawingWorkerPool(Options(2));
        var solveViews=views.Select(v=>v with{Settings=v.Settings!.ForVisibilitySolve()}).ToArray();
        using var local=NativeDrawing.SolveBorrowedAsync(scene.Value,identity,solveViews,new(),default,null).GetAwaiter().GetResult();
        using var worker=pool.SolveAsync(scene.Value,identity,solveViews,new(),default).GetAwaiter().GetResult();
        using var presentedLocal=DrawingPresentation.Apply(local,identity,views,[]);
        using var presentedWorker=DrawingPresentation.Apply(worker,identity,views,[]);
        Assert.True(presentedWorker.Views[0].IgnoredPieces>0);Assert.True(presentedWorker.Views[0].IgnoredObjects>0);
        Assert.Equal(0,presentedWorker.Views[1].IgnoredPieces);
        Assert.NotEmpty(presentedWorker.Views[2].Regions);
        for(var i=0;i<views.Length;i++)
        {
            Assert.Null(worker.Views[i].Error);
            Assert.Equal(WorkerCodec.CaptureDrawing(local.Views[i],true),WorkerCodec.CaptureDrawing(worker.Views[i],true));
            Assert.Equal(WorkerCodec.CaptureDrawing(presentedLocal.Views[i],true),WorkerCodec.CaptureDrawing(presentedWorker.Views[i],true));
            Assert.DoesNotContain(presentedWorker.Views[i].Items,p=>p.Class==LineClass.Hidden);
        }
        // Quarantined hidden pieces must retain their solved classification even when Class changed.
        var hidden=local.Views[0].Pieces.First(p=>p.SolvedClass==LineClass.Hidden);hidden.Class=LineClass.Unresolved;
        using var copied=WorkerCodec.RestoreDrawing(WorkerCodec.CaptureDrawing(local.Views[0]));
        Assert.Equal(LineClass.Hidden,copied.Items.First(p=>p.Class==LineClass.Unresolved).SolvedClass);
    });

}



