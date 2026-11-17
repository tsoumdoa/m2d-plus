using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
internal static class EngineFastBench
{
    internal static void Run(string model,string output,string mode)
    {
        Directory.CreateDirectory(output);
        using var core=new Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc=RhinoDoc.OpenHeadless(model);using var watch=new DocumentWatch(doc);using var cache=new SceneCache();
        using var capture=new SceneCapture(doc,watch,null,cache);while(!capture.Step(TimeSpan.FromMilliseconds(20))){}
        using var scene=capture.Accept();var rows=new List<object>();
        foreach(bool hidden in new[]{false,true})
        {
            var view=ViewRequest.Parse("Top");
            view=view with{Settings=Settings.Resolve([new("v",view.Name,view.Kind,false,scene.Value.Bounds.Diagonal.Length,scene.Value.Tolerance)],
                [["hidden="+(hidden?"on":"off")]],DocumentUnits.Metres).Views[0]};
            var options=new List<NativeHldOptions>();var timer=Stopwatch.StartNew();
            using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new(scene.Value.Revision,view.Revision,"hld",1),[view],
                new(){IsWorker=true,Scheduler=new(12L*1024*1024*1024),ObserveHldOptions=options.Add},default,null).GetAwaiter().GetResult();
            var drawing=batch.Views[0];if(drawing.Error is {} error)throw new InvalidOperationException(error);
            rows.Add(new{hidden,seconds=timer.Elapsed.TotalSeconds,options,drawing.NativeCalls,drawing.Info});
            File.WriteAllBytes(Path.Combine(output,"hld-"+hidden+".json"),WorkerCodec.CaptureDrawing(drawing));
        }
        File.WriteAllText(Path.Combine(output,"engine.json"),JsonSerializer.Serialize(new{model,policy=HldProjection.Policy,fingerprint=WorkerCodec.Fingerprint(),rows}));
    }
}
