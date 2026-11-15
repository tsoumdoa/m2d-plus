using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>One shared batch reservation across components, with one serial native scheduler per process.</summary>
public sealed class DrawingWorkerPool : IDisposable
{
    private sealed class Slot { internal readonly SemaphoreSlim Gate=new(1);internal WorkerClient? Client;internal DateTime LastUsed; }
    private readonly Slot[] _slots;
    private readonly WorkerOptions _options;
    private readonly SemaphoreSlim _batch=new(1);
    private readonly Timer _idle;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _shutdown=new();
    public static DrawingWorkerPool Shared { get; } = new(WorkerOptions.FromEnvironment());
    public DrawingWorkerPool(WorkerOptions options)
    {
        if(options.Count is <1 or >4 || options.MemoryBytes<0 || options.IdleTimeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(options));
        _options=options;_slots=Enumerable.Range(0,options.Count).Select(_=>new Slot()).ToArray();
        _idle=new Timer(_=>ExpireIdle(),null,options.IdleTimeout,options.IdleTimeout);
        AppDomain.CurrentDomain.ProcessExit+=OnExit;
    }
    public bool Installed => File.Exists(Path.Combine(_options.Directory,"M2DPlus.Worker.exe"));
    internal Action<int,int>? SolvingView { get; set; }
    internal Action<int,long,bool>? AttemptingView { get; set; }
    internal Func<WorkerCodec.Reply,WorkerCodec.Reply>? ObserveReply { get; set; }
    internal long PeakBytes => _slots.Sum(s=>s.Client?.PeakBytes??0);
    internal int[] ProcessIds => _slots.Select(s=>s.Client).OfType<WorkerClient>().Select(c=>c.ProcessId).ToArray();
    public WorkerSizing? LastSizing { get; private set; }
    internal Func<int,byte[]>? EncodedRequest { get; set; }
    internal Action<int,byte[]>? EncodedDrawing { get; set; }
    public Task<DrawingBatch> SolveAsync(PreparedScene scene,RequestIdentity identity,IReadOnlyList<ViewRequest> views,
        NativeSettings settings,CancellationToken token,IProgress<JobProgress>? progress=null) =>
        SolveCoreAsync(scene.Cost,identity,views,settings,WorkerCodec.Fingerprint(),new(()=>WorkerCodec.Capture(scene,token),LazyThreadSafetyMode.ExecutionAndPublication),token,progress);
    // Exercises the exact production reservation/transport/retry path without a RhinoCore in the coordinator.
    // Used by the benchmark so four children never imply a fifth licensed Rhino process in their parent.
    internal Task<DrawingBatch> SolveEncodedAsync(SceneCost cost,RequestIdentity identity,IReadOnlyList<ViewRequest> views,
        WorkerFingerprint fingerprint,byte[] scene,CancellationToken token) =>
        SolveCoreAsync(cost,identity,views,new(),fingerprint,new(()=>scene),token,null);
    private async Task<DrawingBatch> SolveCoreAsync(SceneCost cost,RequestIdentity identity,IReadOnlyList<ViewRequest> views,
        NativeSettings settings,WorkerFingerprint fingerprint,Lazy<byte[]> payload,CancellationToken token,IProgress<JobProgress>? progress)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);views=views.ToArray();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_shutdown.Token);token=linked.Token;
        await _batch.WaitAsync(token).ConfigureAwait(false);
        var results=new ViewDrawing?[views.Count];
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            // Idle clients belong to this reservation and can be reclaimed, unlike other applications' memory.
            var memory=_options.ReadMemory();
            long resident=0;
            foreach(var slot in _slots)
            {
                await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try{resident+=slot.Client?.ResidentBytes??0;}finally{slot.Gate.Release();}
            }
            memory=memory with{AvailableBytes=Math.Min(memory.PhysicalBytes,checked(memory.AvailableBytes+resident))};
            var plan=WorkerMemoryPolicy.Size(cost,views.Count,_options.Count,memory,_options.MemoryBytes);LastSizing=plan;
            if(plan.Count==0)throw new WorkerUnavailableException(plan.Refusal!);
            // Retire excess/changed-cap clients before starting any new process. Thus retained idle clients
            // never sit outside the current aggregate reservation.
            for(var i=0;i<_slots.Length;i++)
            {
                var slot=_slots[i];await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try{if(i>=plan.Count || slot.Client?.MemoryCapBytes!=plan.CapBytes){slot.Client?.Dispose();slot.Client=null;}}
                finally{slot.Gate.Release();}
            }
            var retry=new bool[views.Count];var done=0;
            await Task.WhenAll(_slots.Take(plan.Count).Select((slot,shard)=>RunShard(slot,shard))).ConfigureAwait(false);
            if(retry.Any(r=>r))
            {
                // All original native work is over. Release every retained worker before measuring retry headroom.
                await CloseAll().ConfigureAwait(false);
                for(var i=0;i<views.Count;i++)
                {
                    if(!retry[i])continue;token.ThrowIfCancellationRequested();
                    var cap=WorkerMemoryPolicy.RetryCap(plan.CapBytes,_options.ReadMemory());
                    if(cap==0){Failure(i,MemoryFailure(plan.CapBytes,"No larger isolated cap fits the current memory budget."));continue;}
                    var slot=_slots[0];await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            var drawing=await SolveView(slot,i,cap,true).ConfigureAwait(false);
                            if(IsMemoryFailure(drawing.Error) || slot.Client!.MemoryLimitHit)
                            {drawing.Dispose();Failure(i,MemoryFailure(cap,"The one isolated retry also exceeded its cap."));}
                            else {results[i]?.Dispose();results[i]=drawing;drawing._diagnostics.Add($"Worker memory recovery: retried once alone at {cap/(double)WorkerMemoryPolicy.GiB:F1} GiB.");}
                        }
                        catch(Exception ex) when(ex is not OperationCanceledException)
                        {Failure(i,MemoryFailure(cap,"The one isolated retry failed: "+ex.Message));}
                    }
                    finally{slot.Client?.Dispose();slot.Client=null;slot.Gate.Release();}
                }
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new(JobPhase.Complete,views.Count,views.Count));
            return new DrawingBatch(identity,results.Select(r=>r??throw new InvalidDataException("Incomplete worker generation.")).ToList());

            async Task RunShard(Slot slot,int shard)
            {
                await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    for(var i=shard;i<views.Count;i+=plan.Count)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var drawing=await SolveView(slot,i,plan.CapBytes,false).ConfigureAwait(false);
                            if(IsMemoryFailure(drawing.Error) || slot.Client!.MemoryLimitHit)
                            {drawing.Dispose();retry[i]=true;Failure(i,MemoryFailure(plan.CapBytes,"Queued for an isolated retry."));slot.Client?.Dispose();slot.Client=null;}
                            else results[i]=drawing;
                        }
                        catch(WorkerUnavailableException){throw;}
                        catch(Exception ex) when(ex is not OperationCanceledException)
                        {
                            retry[i]=ex is WorkerMemoryException || IsMemoryFailure(ex.Message) || slot.Client?.MemoryLimitHit==true;
                            Failure(i,retry[i]?MemoryFailure(plan.CapBytes,"Queued for an isolated retry."):"Worker failed for this view (crash, timeout or invalid reply): "+ex.Message);
                            slot.Client?.Dispose();slot.Client=null;
                        }
                        progress?.Report(new(JobPhase.Drawing,Interlocked.Increment(ref done),views.Count));
                    }
                }
                finally
                {
                    if(token.IsCancellationRequested){slot.Client?.Dispose();slot.Client=null;}
                    slot.LastUsed=DateTime.UtcNow;slot.Gate.Release();
                }
            }
            async Task<ViewDrawing> SolveView(Slot slot,int index,long cap,bool isolatedRetry)
            {
                slot.Client??=await WorkerClient.StartAsync(_options with{MemoryBytes=cap},fingerprint,token).ConfigureAwait(false);
                if(slot.Client.SceneRevision!=identity.SceneRevision)
                {
                    await slot.Client.CallAsync("scene",payload.Value,_options.SolveTimeout,token).ConfigureAwait(false);
                    slot.Client.SceneRevision=identity.SceneRevision;
                }
                AttemptingView?.Invoke(index,cap,isolatedRetry);
                slot.Client.NativeStarted=()=>SolvingView?.Invoke(slot.Client.ProcessId,index);
                var reply=WorkerCodec.Decode<WorkerCodec.Reply>(await slot.Client.CallAsync("solve",EncodedRequest?.Invoke(index)??WorkerCodec.Encode(new WorkerCodec.Request(identity,index,views[index],settings.NativeOnly)),_options.SolveTimeout,token).ConfigureAwait(false));
                reply=ObserveReply?.Invoke(reply)??reply;
                if(reply.Identity!=identity || reply.ViewIndex!=index)throw new InvalidDataException("Stale worker reply rejected (generation/revisions/view).");
                token.ThrowIfCancellationRequested();ViewDrawing drawing;
                if(EncodedDrawing is {} encoded)
                {
                    encoded(index,reply.Drawing);
                    using var document=System.Text.Json.JsonDocument.Parse(reply.Drawing);var values=document.RootElement.GetProperty("Values");
                    drawing=new ViewDrawing{Name=values.GetProperty("Name").GetString()!,Error=values.GetProperty("Error").GetString()};
                }
                else drawing=WorkerCodec.RestoreDrawing(reply.Drawing);
                drawing._diagnostics.Add($"Worker: isolated whole-view solve; matching runtime and engine; scene retained per process. Reservation={plan.Count} x {plan.CapBytes/(double)WorkerMemoryPolicy.GiB:F1} GiB; budget={plan.BudgetBytes/(double)WorkerMemoryPolicy.GiB:F1} GiB.");
                return drawing;
            }
            void Failure(int index,string message)
            {
                results[index]?.Dispose();results[index]=new ViewDrawing{Name=views[index].Name,Error=message,Report=new("Worker",0,0,new Dictionary<string,int>(),[],true)};
            }
        }
        catch{foreach(var result in results)result?.Dispose();throw;}
        finally
        {
            if(token.IsCancellationRequested)await CloseAll().ConfigureAwait(false);
            _batch.Release();
        }
    }
    private async Task CloseAll()
    {
        foreach(var slot in _slots)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try{slot.Client?.Dispose();slot.Client=null;}finally{slot.Gate.Release();}
        }
    }
    private static bool IsMemoryFailure(string? error)=>error is not null && (error.Contains("memory",StringComparison.OrdinalIgnoreCase) || error.Contains("bad_alloc",StringComparison.OrdinalIgnoreCase));
    private static string MemoryFailure(long cap,string reason)=>$"Worker memory cap ({cap/(double)WorkerMemoryPolicy.GiB:F1} GiB) exceeded for this view. {reason} Close other applications or reduce this scene/view's complexity and run again; no heavy in-process fallback was used.";
    private void ExpireIdle()
    {
        foreach(var slot in _slots)
        {
            if(!slot.Gate.Wait(0))continue;
            try{if(_disposed || DateTime.UtcNow-slot.LastUsed>=_options.IdleTimeout){slot.Client?.Dispose();slot.Client=null;}}
            finally{slot.Gate.Release();}
        }
    }
    private void OnExit(object? sender,EventArgs e)=>Dispose();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_shutdown.Cancel();_idle.Dispose();ExpireIdle();AppDomain.CurrentDomain.ProcessExit-=OnExit;
    }
}

public static class DrawingExecution
{
    internal static async Task<DrawingBatch> SolveBorrowedAsync(PreparedScene scene,RequestIdentity identity,IReadOnlyList<ViewRequest> views,
        NativeSettings settings,CancellationToken token,IProgress<JobProgress>? progress,DrawingWorkerPool? workers=null)
    {
        var pool=workers??DrawingWorkerPool.Shared;
        if((views.Count>1 || !scene.Cost.Admitted(views.Count,settings.Scheduler.BudgetBytes)) && pool.Installed)
        {
            try{return await pool.SolveAsync(scene,identity,views,settings,token,progress).ConfigureAwait(false);}
            catch(WorkerUnavailableException ex)
            {
                if(!scene.Cost.Admitted(views.Count,settings.Scheduler.BudgetBytes))throw new InvalidOperationException(SceneCost.Refusal+"; "+ex.Message,ex);
                var fallback=await NativeDrawing.SolveBorrowedAsync(scene,identity,views,settings,token,progress).ConfigureAwait(false);
                foreach(var view in fallback.Views)view._diagnostics.Add(ex.Message);return fallback;
            }
        }
        return await NativeDrawing.SolveBorrowedAsync(scene,identity,views,settings,token,progress).ConfigureAwait(false);
    }
}
