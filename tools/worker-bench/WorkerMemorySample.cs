using System.ComponentModel;
using System.Diagnostics;
using M2DPlus.Rhino.Services;

/// <summary>Aligned private-byte peaks, including processes that exit during recovery.</summary>
internal sealed class WorkerMemorySample : IDisposable
{
    private readonly object _sync=new();
    private readonly Process _parent=Process.GetCurrentProcess();
    private readonly System.Threading.Timer _timer;
    private bool _stopped;
    internal long WorkerPeak { get; private set; }
    internal long CoordinatorPeak { get; private set; }
    internal long CombinedPeak { get; private set; }
    internal WorkerMemorySample(DrawingWorkerPool pool)
    {
        _timer=new System.Threading.Timer(_=>
        {
            lock(_sync)
            {
                if(_stopped)return;
                long workers=0;
                foreach(var pid in pool.ProcessIds)
                {
                    try{using var process=Process.GetProcessById(pid);workers+=process.PrivateMemorySize64;}
                    catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception){}
                }
                _parent.Refresh();var parent=_parent.PrivateMemorySize64;
                WorkerPeak=Math.Max(WorkerPeak,workers);CoordinatorPeak=Math.Max(CoordinatorPeak,parent);CombinedPeak=Math.Max(CombinedPeak,workers+parent);
            }
        },null,0,25);
    }
    public void Dispose()
    {
        _timer.Dispose();
        lock(_sync){if(_stopped)return;_stopped=true;_parent.Dispose();}
    }
}

