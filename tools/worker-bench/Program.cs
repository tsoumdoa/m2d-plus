using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            if(args[0] is "workers" or "identity" or "pool")
            {
                // Managed value types/archives only in the coordinator; never initialize RhinoCore here.
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>
                {
                    var path=Path.Combine("C:/Program Files/Rhino 8/System",name.Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;
                };
                if(args[0]=="pool"){MeasurePool(args[1],args[2],int.Parse(args[3])).GetAwaiter().GetResult();return 0;}
                if(args[0]=="identity")Inspect(args[1]).GetAwaiter().GetResult();else Measure(args[1],args[2],int.Parse(args[3])).GetAwaiter().GetResult();return 0;
            }
            var resolver=Assembly.LoadFrom(args[1]);resolver.GetType("RhinoInside.Resolver",true)!.GetMethod("Initialize",[typeof(string)])!.Invoke(null,["C:/Program Files/Rhino 8/System"]);
            if(args[0]=="restore")RestoreFrames(args[2]);else Export(args[2],args[3],args[0]!="scene");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Export(string model,string output,bool solve)
    {
        Directory.CreateDirectory(output);
        using var core=new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var document=model=="small"?Rhino.RhinoDoc.CreateHeadless(null):Rhino.RhinoDoc.OpenHeadless(model);
        if(model=="small"){using var box=Rhino.Geometry.Brep.CreateFromBox(new Rhino.Geometry.BoundingBox(Rhino.Geometry.Point3d.Origin,new Rhino.Geometry.Point3d(2,2,2)));document.Objects.AddBrep(box);}
        using var watch=new DocumentWatch(document);using var cache=new SceneCache();
        var clock=Stopwatch.StartNew();using var capture=new SceneCapture(document,watch,null,cache);while(!capture.Step(TimeSpan.FromSeconds(300))){}
        using var lease=capture.Accept();var scene=lease.Value;var captureSeconds=clock.Elapsed.TotalSeconds;
        var names=new[]{"North","South","East","West","Top","SW","Section long overlap","Plan mid"};
        var directions=new[]{"Back","Front","Right","Left","Top","SW"};
        var views=directions.Select((v,i)=>ViewRequest.Parse(v) with{Name=names[i]}).Concat(new[]{ViewRequest.Parse("Section long overlap",document),ViewRequest.Parse("Plan mid",document)}).ToArray();
        // Fallback only when the generated model uses a differently named plan.
        if(views[7].Error is not null)views[7]=ViewRequest.Parse(new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(scene.Bounds.Center.X,scene.Bounds.Center.Y,1.2),Rhino.Geometry.Vector3d.ZAxis)) with{Name="Plan"};
        if(views[6].Error is not null)views[6]=ViewRequest.Parse(new Rhino.Geometry.Plane(scene.Bounds.Center,Rhino.Geometry.Vector3d.XAxis,Rhino.Geometry.Vector3d.ZAxis)) with{Name="Long"};
        var resolved=Settings.Resolve(views.Select((v,i)=>new ViewSettingsContext(i.ToString(),v.Name,v.Kind,v.Cut is not null,scene.Bounds.Diagonal.Length,scene.Tolerance)).ToArray(),new[]{new[]{"hidden = on"}},DocumentUnits.Metres);
        views=views.Select((v,i)=>v with{Settings=resolved.Views[i]}).ToArray();
        if(model=="small")views=[views[4]];
        var identity=new RequestIdentity(scene.Revision,"views","production",1);
        clock.Restart();File.WriteAllBytes(Path.Combine(output,"scene.json"),WorkerCodec.Capture(scene,default));var encodeSeconds=clock.Elapsed.TotalSeconds;
        File.WriteAllBytes(Path.Combine(output,"identity.json"),WorkerCodec.Encode(WorkerCodec.Fingerprint()));
        long peakPrivate=0;using var sample=new System.Threading.Timer(_=>{using var p=Process.GetCurrentProcess();var n=p.PrivateMemorySize64;long current;do{current=Interlocked.Read(ref peakPrivate);if(current>=n)break;}while(Interlocked.CompareExchange(ref peakPrivate,n,current)!=current);},null,0,25);
        var rows=new List<object>();
        for(var i=0;i<views.Length;i++)
        {
            File.WriteAllBytes(Path.Combine(output,$"request-{i}.json"),WorkerCodec.Encode(new WorkerCodec.Request(identity,i,views[i],false)));
            File.WriteAllBytes(Path.Combine(output,"cost.json"),WorkerCodec.Encode(scene.Cost));
            if(!solve)continue;
            clock.Restart();using var batch=NativeDrawing.SolveBorrowedAsync(scene,identity,[views[i]],new(){Scheduler=new(12L*1024*1024*1024),IsWorker=true},default,null).GetAwaiter().GetResult();
            var elapsed=clock.Elapsed.TotalSeconds;var view=batch.Views[0];
            var stable=WorkerCodec.CaptureDrawing(view,true);File.WriteAllBytes(Path.Combine(output,$"drawing-{i}.json"),stable);var hash=WorkerCodec.Hash(stable);
            File.WriteAllText(Path.Combine(output,$"hash-{i}.txt"),hash);
            rows.Add(new{i,view.Name,seconds=elapsed,hash,view.Error,pieces=view.Items.Count,regions=view.Regions.Count,phases=view.Phases.Seconds});
            Console.WriteLine($"{Path.GetFileName(model)} {i} {view.Name}: {elapsed:F3}s {view.Error ?? "OK"}");
            File.WriteAllBytes(Path.Combine(output,"baseline.json"),WorkerCodec.Encode(new{model,modelHash=File.Exists(model)?WorkerCodec.Hash(File.ReadAllBytes(model)):"generated-2x2x2-box",fingerprint=WorkerCodec.Fingerprint(),captureSeconds,encodeSeconds,
                peakPrivateBytes=peakPrivate,cost=scene.Cost,rows}));
        }
    }
    private static async Task MeasurePool(string folder,string workerDirectory,int passes)
    {
        foreach(var name in new[]{"M2DPLUS_WORKERS","M2DPLUS_WORKER_MEMORY_MIB"})
            if(Environment.GetEnvironmentVariable(name) is not null)throw new InvalidOperationException("Default check requires no expert override: "+name);
        var expected=WorkerCodec.Decode<WorkerFingerprint>(File.ReadAllBytes(Path.Combine(folder,"identity.json")));
        var scene=File.ReadAllBytes(Path.Combine(folder,"scene.json"));
        var cost=WorkerCodec.Decode<SceneCost>(File.ReadAllBytes(Path.Combine(folder,"cost.json")));
        var requests=Enumerable.Range(0,Directory.GetFiles(folder,"request-*.json").Length).Select(i=>File.ReadAllBytes(Path.Combine(folder,$"request-{i}.json"))).ToArray();
        using var first=JsonDocument.Parse(requests[0]);var identity=first.RootElement.GetProperty("Identity").Deserialize<RequestIdentity>();
        var views=requests.Select(bytes=>{using var d=JsonDocument.Parse(bytes);return new ViewRequest(d.RootElement.GetProperty("View").GetProperty("Name").GetString()!,default);}).ToArray();
        var options=WorkerOptions.FromEnvironment() with{Directory=workerDirectory};
        using var pool=new DrawingWorkerPool(options);var returned=new byte[views.Length][];
        pool.EncodedRequest=i=>requests[i];pool.EncodedDrawing=(i,bytes)=>{returned[i]=bytes;File.WriteAllBytes(Path.Combine(folder,$"reply-{i}.json"),bytes);Console.WriteLine($"received {i} {views[i].Name}");};
        var memory=options.ReadMemory();var rows=new List<object>();
        for(var pass=0;pass<passes;pass++)
        {
            var clock=Stopwatch.StartNew();
            using var batch=await pool.SolveEncodedAsync(cost,identity,views,expected,scene,default);
            var seconds=clock.Elapsed.TotalSeconds;var peak=pool.PeakBytes;var results=new List<object>();
            for(var i=0;i<views.Length;i++)
            {
                if(batch.Views[i].Error is {} failure)throw new InvalidOperationException($"View {i} failed: {failure}");
                var node=JsonNode.Parse(returned[i])!;node["Values"]!["Seconds"]=0;node["Diagnostics"]=new JsonArray();node["Phases"]=new JsonObject();
                var stable=JsonSerializer.SerializeToUtf8Bytes(node);var hash=WorkerCodec.Hash(stable);
                var baseline=Path.Combine(folder,$"hash-{i}.txt");bool? match=File.Exists(baseline)?hash==File.ReadAllText(baseline):null;
                File.WriteAllText(Path.Combine(folder,$"reply-hash-{i}.txt"),hash);
                results.Add(new{i,name=views[i].Name,hash,match,pieces=node["Pieces"]!.AsArray().Count,regions=node["Regions"]!.AsArray().Count});
                Console.WriteLine($"pass {pass} {i}: match={match} error=none");
                if(match==false){File.WriteAllBytes(Path.Combine(folder,$"pool-mismatch-{pass}-{i}.json"),stable);throw new InvalidOperationException("Hash mismatch");}
            }
            rows.Add(new{pass,seconds,initialMemory=memory,sizing=pool.LastSizing,peakCommitUpperBound=peak,views=results});
            File.WriteAllBytes(Path.Combine(folder,"pool-defaults.json"),WorkerCodec.Encode(rows));
            Console.WriteLine($"pass {pass}: {seconds:F3}s; {pool.LastSizing}");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void RestoreFrames(string folder)
    {
        using var core=new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        var rows=new List<object>();
        for(var i=0;i<8;i++)
        {
            using var drawing=WorkerCodec.RestoreDrawing(File.ReadAllBytes(Path.Combine(folder,$"reply-{i}.json")));
            if(drawing.Error is {} error)throw new InvalidOperationException(error);
            var hash=WorkerCodec.Hash(WorkerCodec.CaptureDrawing(drawing,true));
            var match=hash==File.ReadAllText(Path.Combine(folder,$"reply-hash-{i}.txt"));
            if(!match)throw new InvalidOperationException("Native restoration hash mismatch: "+i);
            rows.Add(new{i,drawing.Name,hash,match,pieces=drawing.Items.Count,regions=drawing.Regions.Count});
            Console.WriteLine($"native restored {i} {drawing.Name}: identical={match}, pieces={drawing.Items.Count}, regions={drawing.Regions.Count}");
        }
        File.WriteAllBytes(Path.Combine(folder,"native-restoration.json"),WorkerCodec.Encode(rows));
    }
    private static async Task Inspect(string directory)
    {
        var name="m2d-inspect-"+Guid.NewGuid().ToString("N");
        using var pipe=new System.IO.Pipes.NamedPipeServerStream(name,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly);
        using var job=new WorkerJob(6L*1024*1024*1024);
        var start=new ProcessStartInfo(Path.Combine(directory,"M2DPlus.Worker.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
        start.Environment["DOTNET_ROLL_FORWARD"]="LatestPatch";start.ArgumentList.Add(name);start.ArgumentList.Add("C:/Program Files/Rhino 8/System");
        using var process=Process.Start(start)!;job.Assign(process);await process.StandardInput.WriteLineAsync("contained");process.StandardInput.Close();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(45));using var kill=cancel.Token.Register(job.Kill);
        try{await pipe.WaitForConnectionAsync(cancel.Token);Console.WriteLine(System.Text.Encoding.UTF8.GetString(await WorkerFrames.ReadAsync(pipe,cancel.Token)));}
        finally{job.Kill();process.WaitForExit();}
    }
    private static async Task Measure(string folder,string workerDirectory,int count)
    {
        var expected=WorkerCodec.Decode<WorkerFingerprint>(File.ReadAllBytes(Path.Combine(folder,"identity.json")));
        var scene=File.ReadAllBytes(Path.Combine(folder,"scene.json"));
        var viewCount=Directory.GetFiles(folder,"request-*.json").Length;
        var requests=Enumerable.Range(0,viewCount).Select(i=>File.ReadAllBytes(Path.Combine(folder,$"request-{i}.json"))).ToArray();
        var options=new WorkerOptions{Count=count,Directory=workerDirectory,RhinoSystem="C:/Program Files/Rhino 8/System",MemoryBytes=6L*1024*1024*1024,SolveTimeout=TimeSpan.FromMinutes(20)};
        var clients=new WorkerClient?[count];var rows=new List<object>();
        try
        {
            for(var pass=0;pass<2;pass++)
            {
                var wall=Stopwatch.StartNew();var perView=new object[viewCount];var returned=new byte[viewCount][];var elapsed=new double[viewCount];
                await Task.WhenAll(Enumerable.Range(0,count).Select(async shard=>
                {
                    if(clients[shard] is null)
                    {
                        clients[shard]=await WorkerClient.StartAsync(options,expected,default);
                        await clients[shard]!.CallAsync("scene",scene,options.SolveTimeout,default);
                    }
                    for(int i=shard;i<viewCount;i+=count)
                    {
                        var clock=Stopwatch.StartNew();var reply=WorkerCodec.Decode<WorkerCodec.Reply>(await clients[shard]!.CallAsync("solve",requests[i],options.SolveTimeout,default));
                        elapsed[i]=clock.Elapsed.TotalSeconds;returned[i]=reply.Drawing;Console.WriteLine($"{count} workers pass {pass}: received view {i} in {elapsed[i]:F3}s");

                    }
                }));
                var seconds=wall.Elapsed.TotalSeconds;
                for(var i=0;i<viewCount;i++)
                {
                    var node=JsonNode.Parse(returned[i])!;var error=node["Values"]!["Error"]?.GetValue<string>();var name=node["Values"]!["Name"]!.GetValue<string>();
                    node["Values"]!["Seconds"]=0;node["Diagnostics"]=new JsonArray();node["Phases"]=new JsonObject();
                    var stable=JsonSerializer.SerializeToUtf8Bytes(node);var hash=WorkerCodec.Hash(stable);
                    var match=hash==File.ReadAllText(Path.Combine(folder,$"hash-{i}.txt"));
                    perView[i]=new{i,name,seconds=elapsed[i],hash,match,error};Console.WriteLine($"{count} workers pass {pass}: {i} {name} {elapsed[i]:F3}s match={match} error={error}");
                    if(!match)File.WriteAllBytes(Path.Combine(folder,$"mismatch-{count}-{pass}-{i}.json"),stable);
                }
                rows.Add(new{count,pass,cold=pass==0,seconds,peakCommitUpperBound=clients.Sum(c=>c!.PeakBytes),perView});
                File.WriteAllBytes(Path.Combine(folder,$"workers-{count}.json"),WorkerCodec.Encode(rows));
            }
        }
        finally{foreach(var client in clients)client?.Dispose();}
    }
}

