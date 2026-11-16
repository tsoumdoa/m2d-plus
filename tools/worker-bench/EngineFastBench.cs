using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;

internal static class EngineFastBench
{
    internal static void Run(string model,string output,string mode)
    {
        Directory.CreateDirectory(output);
        if(File.Exists(Path.Combine(output,"engine.json")))throw new IOException("Evidence already exists");
        using var core=new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc=RhinoDoc.OpenHeadless(model);using var watch=new DocumentWatch(doc);using var cache=new SceneCache();
        var clock=Stopwatch.StartNew();using var capture=new SceneCapture(doc,watch,null,cache);
        while(!capture.Step(TimeSpan.FromMilliseconds(20))){}
        using var lease=capture.Accept();var scene=lease.Value;double captureSeconds=clock.Elapsed.TotalSeconds;
        if(mode=="generate-planar")
        {
            string target=Path.Combine(output,"perf-M-blocks-planar.3dm");
            if(File.Exists(target))throw new IOException("Never overwrite model evidence: "+target);
            var prepared=scene.Prepare(default);
            var removed=prepared.Where(s=>!new PlanarExtraction().Add(s.Geometry,Transform.Identity,Plane.WorldXY,scene.Tolerance,s.Identity,nativeMeshCandidates:true))
                .Select(s=>Guid.Parse(s.Identity.Split('/')[0])).Distinct().ToArray();
            var descriptions=removed.Select(id=>new{id,name=doc.Objects.FindId(id)?.Name}).ToArray();
            foreach(var id in removed)if(!doc.Objects.Delete(id,true))throw new InvalidOperationException("Could not remove nonplanar source");
            if(!doc.Write3dmFile(target,new Rhino.FileIO.FileWriteOptions()))throw new IOException("Model write failed");
            File.WriteAllText(target+".generation.json",JsonSerializer.Serialize(new{source=model,sourceHash=WorkerCodec.Hash(File.ReadAllBytes(model)),target,
                method="Copy headless document; remove entire top-level sources having a leaf rejected by complete planar extraction. Retain remaining blocks, IDs, transforms, units and tolerance; no geometry approximation.",
                before=scene.SourceCount,removed=descriptions,tolerance=scene.Tolerance}));
            Console.WriteLine($"Generated planar derivative by removing {removed.Length} nonplanar top-level sources");
            return;
        }
        bool building=mode is "building" or "planar";
        var units=doc.ModelUnitSystem==UnitSystem.Millimeters?DocumentUnits.Millimetres:DocumentUnits.Metres;
        var directions=building?new[]{"Back","Front","Right","Left","Top","SW"}:new[]{"Back"};
        var views=directions.Select(v=>ViewRequest.Parse(v)).ToList();
        if(building)
        {
            views.Add(ViewRequest.Parse(new Plane(scene.Bounds.Center,Vector3d.XAxis,Vector3d.ZAxis)) with{Name="Long section"});
            views.Add(ViewRequest.Parse(new Plane(new Point3d(scene.Bounds.Center.X,scene.Bounds.Center.Y,1.2*RhinoMath.UnitScale(UnitSystem.Meters,doc.ModelUnitSystem)),Vector3d.ZAxis)) with{Name="Plan 1.2m"});
        }
        var rows=new List<object>();var references=new Dictionary<(int,bool),string>();
        void Save()=>File.WriteAllText(Path.Combine(output,"engine.json"),JsonSerializer.Serialize(new{model,mode,modelHash=WorkerCodec.Hash(File.ReadAllBytes(model)),units=doc.ModelUnitSystem.ToString(),tolerance=scene.Tolerance,
            scene.SourceCount,captureSeconds,fingerprint=WorkerCodec.Fingerprint(),rows}));
        Save();
        // Same owned scene; each cell recomputes visibility, bypassing result cache.
        // This explicit isolated in-process harness uses a 12 GiB admission budget,
        // as in ScaleBench; it does not change product dispatch or memory policy.
        foreach(bool fast in new[]{false,true})
        foreach(bool hidden in new[]{false,true})
        for(int i=0;i<views.Count;i++)
        {
            var request=Resolve(i,fast,hidden);var solve=request with{Settings=request.Settings!.ForVisibilitySolve()};
            var native=new List<NativeHldOptions>();
            string label=$"{(fast?"fast":"exact")}-{(hidden?"on":"off")}-{i}";
            var identity=new RequestIdentity(scene.Revision,request.Revision,label,rows.Count+1);
            var progress=new Progress(clock,Path.Combine(output,label+"-progress.json"));
            clock.Restart();
            using var raw=NativeDrawing.SolveBorrowedAsync(scene,identity,[solve],new(){IsWorker=true,Scheduler=new(12L*1024*1024*1024),ObserveHldOptions=native.Add},default,progress).GetAwaiter().GetResult();
            double computeSeconds=clock.Elapsed.TotalSeconds;
            if(raw.Views[0].Error is {} error)throw new InvalidOperationException(error);
            clock.Restart();using var display=DrawingPresentation.Apply(raw,identity,[request],[]);
            double presentationSeconds=clock.Elapsed.TotalSeconds;
            var drawing=display.Views[0];
            // Only normalize authored engine metadata (and the hidden-on raw
            // normalization already undone by presentation). Retain every output
            // geometry, class, provenance, quality and routing field in the hash.
            var dto=WorkerCodec.Decode<WorkerCodec.Drawing>(WorkerCodec.CaptureDrawing(drawing,true));
            var normalized=WorkerCodec.Encode(dto with{Values=dto.Values with{Settings=Resolve(i,false,hidden).Settings}});
            var hash=WorkerCodec.Hash(normalized);
            bool? match=null;
            if(!fast)references[(i,hidden)]=hash;
            else match=hash==references[(i,hidden)];
            rows.Add(new{i,request.Name,engine=fast?"fast":"exact",hidden,computeSeconds,presentationSeconds,wallSeconds=computeSeconds+presentationSeconds,
                raw.Views[0].Seconds,native,rawPhases=raw.Views[0].Phases.Seconds,presentationPhases=drawing.Phases.Seconds,
                drawing.NativeCalls,drawing.FastNativeSources,drawing.MultipleThreads,pieces=drawing.Items.Count,regions=drawing.Regions.Count,
                counts=drawing.Items.GroupBy(p=>p.Class.ToString()).ToDictionary(g=>g.Key,g=>g.Count()),hash,planarHashMatch=match,info=drawing.Info});
            Save();
            File.WriteAllBytes(Path.Combine(output,label+"-drawing.json"),WorkerCodec.CaptureDrawing(drawing,true));
            Console.WriteLine($"{Path.GetFileName(model)} {label} {request.Name}: {computeSeconds+presentationSeconds:F3}s; native={drawing.NativeCalls}; fast sources={drawing.FastNativeSources}; hash match={match}");
            if(mode=="planar"&&(drawing.NativeCalls!=0||drawing.FastNativeSources!=0||match==false))throw new InvalidOperationException("Planar exact/fast equality gate failed");
        }
        ViewRequest Resolve(int i,bool fast,bool hidden)
        {
            var view=views[i];
            var context=new ViewSettingsContext(i.ToString(),view.Name,view.Kind,view.Cut is not null,scene.Bounds.Diagonal.Length,scene.Tolerance);
            var settings=Settings.Resolve([context],[[$"engine={(fast?"fast":"exact")}\nhidden={(hidden?"on":"off")}"]],units);
            if(settings.HasErrors)throw new InvalidOperationException("Settings failed");
            return view with{Settings=settings.Views[0]};
        }
    }
    private sealed class Progress(Stopwatch watch,string path):IProgress<JobProgress>
    {
        private long _last;
        public void Report(JobProgress value)
        {
            lock(this)
            {
                long now=Stopwatch.GetTimestamp();if(Stopwatch.GetElapsedTime(_last,now).TotalSeconds<1)return;_last=now;
                File.WriteAllText(path,JsonSerializer.Serialize(new{seconds=watch.Elapsed.TotalSeconds,value}));
            }
        }
    }
}
