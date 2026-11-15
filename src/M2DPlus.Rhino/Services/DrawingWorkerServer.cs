using System.Text;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Runs only in the contained child after RhinoCore has initialized. No document access.</summary>
public static class DrawingWorkerServer
{
    public static void Run(Stream pipe)
    {
        var own=WorkerCodec.Fingerprint();
        WorkerFrames.WriteAsync(pipe,WorkerCodec.Encode(own),default).GetAwaiter().GetResult();
        var expected=WorkerCodec.Decode<WorkerFingerprint>(WorkerFrames.ReadAsync(pipe,default).GetAwaiter().GetResult());
        if(!own.Accepts(expected))throw new InvalidDataException("Host fingerprint mismatch; scene refused.");
        var budget=long.Parse(Environment.GetEnvironmentVariable("M2DPLUS_WORKER_BUDGET") ?? "4294967296",System.Globalization.CultureInfo.InvariantCulture);
        var scheduler=new NativeScheduler(budget);
        PreparedScene? scene=null;
        try
        {
            while(true)
            {
                WorkerCodec.Message request;
                try{request=WorkerCompression.Decode<WorkerCodec.Message>(WorkerFrames.ReadAsync(pipe,default).GetAwaiter().GetResult());}
                catch(EndOfStreamException){return;}
                byte[] payload;
                try
                {
                    switch(request.Kind)
                    {
                        case "scene":
                            scene?.Dispose();scene=null;scene=WorkerCodec.RestoreScene(request.Payload);payload=[];break;
                        case "solve":
                            var solve=WorkerCodec.Decode<WorkerCodec.Request>(request.Payload);
                            if(scene is null || scene.Revision!=solve.Identity.SceneRevision)throw new InvalidDataException("Scene revision not loaded.");
                            var signalled=false;
                            NativeDrawing.NativeStarting=()=>
                            {
                                if(signalled)return;signalled=true;
                                WorkerFrames.WriteAsync(pipe,WorkerCompression.Encode(new WorkerCodec.Message("started",[])),default).GetAwaiter().GetResult();
                                // Internal licensed fault injection, never exposed in GH or settings.
                                switch(Environment.GetEnvironmentVariable("M2DPLUS_WORKER_TEST_FAULT"))
                                {
                                    case "crash":Environment.FailFast("Injected owned-worker crash");break;
                                    case "block":Thread.Sleep(Timeout.Infinite);break;
                                    case "memory-once" when budget < 2L*1024*1024*1024:
                                    case "memory":
                                        var allocation=System.Runtime.InteropServices.Marshal.AllocHGlobal(checked((nint)(budget*2)));
                                        System.Runtime.InteropServices.Marshal.FreeHGlobal(allocation);throw new InvalidOperationException("Memory cap failed to refuse allocation.");
                                }
                            };
                            using(var result=NativeDrawing.SolveBorrowedAsync(scene,solve.Identity,[solve.View],
                                new NativeSettings(false,solve.NativeOnly){Scheduler=scheduler,IsWorker=true},default,null).GetAwaiter().GetResult())
                                payload=WorkerCodec.Encode(new WorkerCodec.Reply(solve.Identity,solve.ViewIndex,WorkerCodec.CaptureDrawing(result.Views[0])));
                            break;
                        default:throw new InvalidDataException("Unknown worker operation.");
                    }
                    WorkerFrames.WriteAsync(pipe,WorkerCompression.Encode(new WorkerCodec.Message(request.Kind,payload)),default).GetAwaiter().GetResult();
                }
                catch(Exception ex)
                {
                    WorkerFrames.WriteAsync(pipe,WorkerCompression.Encode(new WorkerCodec.Message(ex is OutOfMemoryException ? "memory" : "error",Encoding.UTF8.GetBytes(ex.Message))),default).GetAwaiter().GetResult();
                }
            }
        }
        finally{scene?.Dispose();}
    }
}
