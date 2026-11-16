using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace M2DPlus.HcPerf;

internal static class Program
{
    private sealed record Case(string path, string view, string output, bool freshNative, bool publication, bool traceNative, bool? joints);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || Console.ReadLine() != "go") throw new InvalidOperationException("Owned-job handshake and request/resolver paths required");
        var resolver = Assembly.LoadFrom(args[1]);
        resolver.GetType("RhinoInside.Resolver",true)!.GetMethod("Initialize",[typeof(string)])!.Invoke(null,[@"C:\Program Files\Rhino 8\System"]);
        return Run(args[0]);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string request)
    {
        using var core = new global::Rhino.Runtime.InProcess.RhinoCore(["/nosplash","/notemplate"],global::Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        using var primary = global::Rhino.RhinoDoc.CreateHeadless(null);
        global::Rhino.RhinoDoc.ActiveDoc = primary;
        foreach (var c in JsonSerializer.Deserialize<Case[]>(File.ReadAllText(request))!)
            CompositionGate.Run(c.path,c.view,c.output,c.freshNative,c.publication,c.traceNative,c.joints??true);
        return 0;
    }
}
