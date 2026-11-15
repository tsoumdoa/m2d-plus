using System.Runtime.CompilerServices;
using M2DPlus.Rhino.Services;
using Rhino.Runtime.InProcess;

internal static class WorkerHost
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(Stream pipe)
    {
        using var core=new RhinoCore(["/nosplash","/notemplate"],WindowStyle.NoWindow);
        DrawingWorkerServer.Run(pipe);
    }
}
