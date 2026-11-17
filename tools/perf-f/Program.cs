using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
namespace M2DPlus.PerfF;
internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length<4)throw new ArgumentException("Usage: resolver model.3dm output-directory Top|Front|SW|plan|section");
        Assembly.LoadFrom(args[0]).GetType("RhinoInside.Resolver",true)!.GetMethod("Initialize",[typeof(string)])!.Invoke(null,["C:/Program Files/Rhino 8/System"]);
        return Run(args);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Run(string[] args)
    {
        using var core=new global::Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],global::Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var doc=RhinoDoc.OpenHeadless(args[1]);using var watch=new DocumentWatch(doc);using var cache=new SceneCache();
        using var capture=new SceneCapture(doc,watch,null,cache);while(!capture.Step(TimeSpan.FromMilliseconds(20))){}
        using var scene=capture.Accept();Directory.CreateDirectory(args[2]);
        var view=args[3] is "plan" or "section"?ViewRequest.Parse(new Plane(scene.Value.Bounds.Center,Vector3d.ZAxis)):ViewRequest.Parse(args[3]);
        var options=new List<NativeHldOptions>();var clock=Stopwatch.StartNew();
        using var batch=NativeDrawing.SolveBorrowedAsync(scene.Value,new(scene.Value.Revision,view.Revision,"hld",1),[view],new(){ObserveHldOptions=options.Add},default,null).GetAwaiter().GetResult();
        var drawing=batch.Views[0];
        File.WriteAllBytes(Path.Combine(args[2],"drawing.json"),WorkerCodec.CaptureDrawing(drawing));
        File.WriteAllText(Path.Combine(args[2],"manifest.json"),JsonSerializer.Serialize(new{model=args[1],view=args[3],seconds=clock.Elapsed.TotalSeconds,
            policy=HldProjection.Policy,fingerprint=WorkerCodec.Fingerprint(),processorCount=Environment.ProcessorCount,options,drawing.Info}));
        return drawing.Error is null?0:1;
    }
}
