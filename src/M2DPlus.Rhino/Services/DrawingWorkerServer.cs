using System.Text;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Runs only in the contained child after RhinoCore has initialized. No document access.</summary>
public static class DrawingWorkerServer
{
    public static void Run(Stream pipe)
    {
        var writeGate = new object();
        void Send(string kind, byte[] data)
        { lock(writeGate) WorkerFrames.WriteAsync(pipe,WorkerPackets.Encode(kind,data),default).GetAwaiter().GetResult(); }
        var progress = new ProgressRelay(writeGate,data=>WorkerFrames.WriteAsync(pipe,WorkerPackets.Encode("progress",data),default).GetAwaiter().GetResult());
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
                WorkerPacket request;
                try{request=WorkerPackets.Decode(WorkerFrames.ReadAsync(pipe,default).GetAwaiter().GetResult());}
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
                                Send("started",[]);
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
                            progress.Begin();
                            try
                            {
                                using var result=NativeDrawing.SolveBorrowedAsync(scene,solve.Identity,[solve.View],
                                    new NativeSettings(false,solve.NativeOnly){Scheduler=scheduler,IsWorker=true,DenseAudit=solve.DenseAudit,DenseAuditOptions=solve.DenseAuditOptions??new()},default,progress).GetAwaiter().GetResult();
                                payload=WorkerCodec.Encode(new WorkerCodec.Reply(solve.Identity,solve.ViewIndex,WorkerCodec.CaptureDrawing(result.Views[0])));
                            }
                            finally{progress.End();}
                            break;
                        default:throw new InvalidDataException("Unknown worker operation.");
                    }
                    Send(request.Kind,payload);
                }
                catch(Exception ex)
                {
                    Send(ex is OutOfMemoryException ? "memory" : "error",Encoding.UTF8.GetBytes(ex.Message));
                }
            }
        }
        finally{scene?.Dispose();}
    }
    /// <summary>
    /// The solve's inner progress for the host's status (UX §5.2): only preparing and the view's step with its fraction
    /// (the host owns view counts and completion). A new step goes out at once, a fraction at most every 100 ms, never
    /// after the reply, and never blocking a native or kernel thread behind another write.
    /// </summary>
    private sealed class ProgressRelay(object writeGate, Action<byte[]> write) : IProgress<JobProgress>
    {
        private bool _active;
        private string? _step;
        private long _last;
        internal void Begin() { lock(writeGate){_active=true;_step=null;_last=0;} }
        internal void End() { lock(writeGate)_active=false; }
        public void Report(JobProgress value)
        {
            JobProgress forward;
            if(value.Phase==JobPhase.Preparing)
                forward=new(JobPhase.Preparing,value.Done,value.Total){Step="preparing",StepFraction=value.Total>0?Math.Clamp((double)value.Done/value.Total,0,1):null};
            else if(value.Phase==JobPhase.Drawing && value.Step is not null)
                forward=new(JobPhase.Drawing,value.Done,value.Total){View=value.View,Step=value.Step,StepFraction=value.StepFraction};
            else return;
            if(!Monitor.TryEnter(writeGate))return; // a reply or another report is being written: skip this one
            try
            {
                var now=System.Diagnostics.Stopwatch.GetTimestamp();
                if(!_active || forward.Step==_step && System.Diagnostics.Stopwatch.GetElapsedTime(_last,now).TotalMilliseconds<100)return;
                _step=forward.Step;_last=now;
                write(WorkerCodec.Encode(forward));
            }
            finally{Monitor.Exit(writeGate);}
        }
    }
}
