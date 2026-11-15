using System.Runtime.CompilerServices;
using M2DPlus.Rhino.Services;
using Rhino.Runtime.InProcess;

internal static class WorkerHost
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(Stream pipe)
    {
        // Internal fault injection: one contained process stalls before Rhino or
        // handshake, while its siblings initialize normally. Never a GH setting.
        const string prefix="startup-once|";
        if(Environment.GetEnvironmentVariable("M2DPLUS_WORKER_TEST_FAULT") is {} fault && fault.StartsWith(prefix,StringComparison.Ordinal))
        {
            var marker=fault[prefix.Length..];var first=false;
            try{using var file=new FileStream(marker,FileMode.CreateNew,FileAccess.Write);first=true;}
            catch(IOException) when(File.Exists(marker)){}
            if(first)Thread.Sleep(Timeout.Infinite);
        }
        using var core=new RhinoCore(["/nosplash","/notemplate"],WindowStyle.NoWindow);
        DrawingWorkerServer.Run(pipe);
    }
}
