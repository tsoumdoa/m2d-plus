using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>One shared batch reservation across components, with one serial native scheduler per process.</summary>
public sealed class DrawingWorkerPool : IDisposable
{
    private sealed class Slot { internal readonly SemaphoreSlim Gate=new(1);internal WorkerClient? Client;internal DateTime LastUsed; }
    private readonly Slot[] _slots;
    private readonly WorkerOptions _options;
    private readonly SemaphoreSlim _batch=new(1);
    private readonly SemaphoreSlim _replies=new(1);
    private readonly Timer _idle;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _shutdown=new();
    private string? _historyScene;
    private Dictionary<string,double> _viewSeconds=new(StringComparer.Ordinal);
    public static DrawingWorkerPool Shared { get; } = new(WorkerOptions.FromEnvironment());
    public DrawingWorkerPool(WorkerOptions options)
    {
        if(options.Count<1 || options.Count>WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount) || options.HldWorkerLimit<1 || options.HldWorkerLimit>WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount) || options.MemoryBytes<0 || options.IdleTimeout<=TimeSpan.Zero || options.StartupTimeout<=TimeSpan.Zero || options.SolveTimeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(options));
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
            long resident=0;bool retained=false;
            foreach(var slot in _slots)
            {
                await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try{resident+=slot.Client?.ResidentBytes??0;retained|=slot.Client is not null;}finally{slot.Gate.Release();}
            }
            memory=memory with{AvailableBytes=Math.Min(memory.PhysicalBytes,checked(memory.AvailableBytes+resident))};
            long extra=0;
            var admittedWorkers=Math.Min(_options.Count,_options.CurrentHldWorkerLimit);
            var plan=WorkerMemoryPolicy.Size(cost,views.Count,admittedWorkers,memory,_options.MemoryBytes,extra);
            if(plan.Count==0 && retained)
            {
                // Working-set credit cannot restore paged-out private commit. Reap only our idle clients,
                // then use actual fresh RAM/commit before refusing; never credit another application's memory.
                await CloseAll().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                memory=_options.ReadMemory();
                plan=WorkerMemoryPolicy.Size(cost,views.Count,admittedWorkers,memory,_options.MemoryBytes,extra);
            }
            LastSizing=plan;
            if(plan.Count==0)throw new WorkerUnavailableException(plan.Refusal!);
            var processors=Math.Max(1,WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount)/plan.Count);
            // Retire excess/changed-cap clients before starting any new process. Thus retained idle clients
            // never sit outside the current aggregate reservation.
            for(var i=0;i<_slots.Length;i++)
            {
                var slot=_slots[i];await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try{if(i>=plan.Count || slot.Client?.MemoryCapBytes!=plan.CapBytes || slot.Client?.ProcessorLimit!=processors){slot.Client?.Dispose();slot.Client=null;}}
                finally{slot.Gate.Release();}
            }
            if(_historyScene!=identity.SceneRevision){_viewSeconds.Clear();_historyScene=identity.SceneRevision;}
            var keys=views.Select(v=>WorkerCodec.TimingKey(v,settings,fingerprint)).ToArray();
            var order=Enumerable.Range(0,views.Count).OrderByDescending(i=>_viewSeconds.GetValueOrDefault(keys[i])).ThenBy(i=>i).ToArray();
            var pending=new System.Collections.Concurrent.ConcurrentQueue<int>(order);var seconds=new double[views.Count];
            progress?.Report(new(JobPhase.Drawing,0,views.Count){Workers=plan.Count,Step="starting workers"});
            var retry=new bool[views.Count];var retryPeaks=new long[views.Count];var done=0;
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(token);
            var shownSync=new object();var shown=-1;long shownAt=0; // the view whose inner step the status shows
            var unavailable=new System.Collections.Concurrent.ConcurrentDictionary<Slot,byte>();
            WorkerUnavailableException? startupFailure=null;
            // A process does not own a view until its handshake succeeds. Slow startup
            // must not strand work that a ready sibling could take from the queue.
            while(!pending.IsEmpty)
            {
                var active=_slots.Take(plan.Count).Where(s=>!unavailable.ContainsKey(s)).ToArray();
                if(active.Length==0)break;
                await Task.WhenAll(active.Select(RunShard)).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            // One failed handshake must not discard views completed by matching
            // siblings. If nobody could start, retain the unavailable/fallback contract.
            if(startupFailure is not null)
            {
                if(!results.Any(r=>r is not null))throw new WorkerUnavailableException("No worker can take the queued views after startup failures: "+startupFailure.Message);
                for(var i=0;i<results.Length;i++)if(results[i] is null)
                    Failure(i,"No worker can take this queued view after startup failures: "+startupFailure.Message);
            }
            if(retry.Any(r=>r))
            {
                // All original native work is over. Release every retained worker before measuring retry headroom.
                await CloseAll().ConfigureAwait(false);
                for(var i=0;i<views.Count;i++)
                {
                    if(!retry[i])continue;token.ThrowIfCancellationRequested();
                    var retryMemory=_options.ReadMemory();
                    var cap=WorkerMemoryPolicy.RetryCap(plan.CapBytes,retryMemory,retryPeaks[i]);
                    if(cap==0){Failure(i,MemoryFailure(plan.CapBytes,"No larger isolated cap fits the current memory budget."));continue;}
                    var slot=_slots[0];await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            var limit=WorkerMemoryPolicy.ReplyExpandedLimit(WorkerMemoryPolicy.HostReplyBytes(WorkerMemoryPolicy.Budget(retryMemory)));
                            var drawing=await SolveView(slot,i,cap,true,limit).ConfigureAwait(false);
                            if(IsMemoryFailure(drawing.Error) || slot.Client!.MemoryLimitHit)
                            {drawing.Dispose();Failure(i,MemoryFailure(cap,"The one isolated retry also exceeded its cap."));}
                            else {results[i]?.Dispose();results[i]=drawing;drawing._diagnostics.Add($"Worker memory recovery: retried once alone at {cap/(double)WorkerMemoryPolicy.GiB:F1} GiB (observed job peak {retryPeaks[i]/(double)WorkerMemoryPolicy.GiB:F2} GiB).");}
                        }
                        catch(Exception ex) when(ex is not OperationCanceledException)
                        {Failure(i,MemoryFailure(cap,"The one isolated retry failed: "+ex.Message));}
                    }
                    finally{slot.Client?.Dispose();slot.Client=null;slot.Gate.Release();}
                }
            }
            token.ThrowIfCancellationRequested();
            // Keep only this batch's history, bounded by the current view set. Failures
            // don't train the predictor. Scheduling never changes publication order.
            _viewSeconds=Enumerable.Range(0,views.Count).Where(i=>results[i]?.Error is null && seconds[i]>0)
                .GroupBy(i=>keys[i],StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Max(i=>seconds[i]),StringComparer.Ordinal);
            progress?.Report(new(JobPhase.Complete,views.Count,views.Count));
            return new DrawingBatch(identity,results.Select(r=>r??throw new InvalidDataException("Incomplete worker generation.")).ToList());

            async Task RunShard(Slot slot)
            {
                await slot.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    while(!pending.IsEmpty)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            slot.Client??=await WorkerClient.StartAsync(_options with{MemoryBytes=plan.CapBytes,ProcessorLimit=processors},fingerprint,startup.Token).ConfigureAwait(false);
                        }
                        catch(OperationCanceledException) when(startup.IsCancellationRequested && !token.IsCancellationRequested)
                        { break; } // All views finished elsewhere; StartAsync has reaped its owned process.
                        catch(WorkerUnavailableException ex)
                        {
                            unavailable.TryAdd(slot,0);Interlocked.CompareExchange(ref startupFailure,ex,null);
                            break;
                        }
                        if(!pending.TryDequeue(out var i))break;
                        try
                        {
                            var drawing=await SolveView(slot,i,plan.CapBytes,false,plan.ReplyExpandedLimit).ConfigureAwait(false);
                            if(IsMemoryFailure(drawing.Error) || slot.Client!.MemoryLimitHit)
                            {retryPeaks[i]=slot.Client!.PeakBytes;drawing.Dispose();retry[i]=true;Failure(i,MemoryFailure(plan.CapBytes,"Queued for an isolated retry."));slot.Client?.Dispose();slot.Client=null;}
                            else results[i]=drawing;
                        }
                        catch(WorkerUnavailableException ex)
                        {
                            unavailable.TryAdd(slot,0);Interlocked.CompareExchange(ref startupFailure,ex,null);
                            pending.Enqueue(i);
                            break;
                        }
                        catch(Exception ex) when(ex is not OperationCanceledException)
                        {
                            retry[i]=ex is WorkerMemoryException || IsMemoryFailure(ex.Message) || slot.Client?.MemoryLimitHit==true;
                            if(retry[i])retryPeaks[i]=slot.Client?.PeakBytes??0;
                            Failure(i,retry[i]?MemoryFailure(plan.CapBytes,"Queued for an isolated retry."):"Worker failed for this view (crash, timeout or invalid reply): "+ex.Message);
                            slot.Client?.Dispose();slot.Client=null;
                        }
                        var completed=Interlocked.Increment(ref done);
                        if(completed==views.Count)startup.Cancel();
                        progress?.Report(new(JobPhase.Drawing,completed,views.Count){Workers=plan.Count});
                    }
                }
                finally
                {
                    if(token.IsCancellationRequested){slot.Client?.Dispose();slot.Client=null;}
                    slot.LastUsed=DateTime.UtcNow;slot.Gate.Release();
                }
            }
            async Task<ViewDrawing> SolveView(Slot slot,int index,long cap,bool isolatedRetry,long replyLimit)
            {
                slot.Client??=await WorkerClient.StartAsync(_options with{MemoryBytes=cap,ProcessorLimit=isolatedRetry?WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount):processors},fingerprint,token).ConfigureAwait(false);
                if(slot.Client.SceneRevision!=identity.SceneRevision)
                {
                    progress?.Report(new(JobPhase.Drawing,Volatile.Read(ref done),views.Count){Workers=plan.Count,Step="sending scene"});
                    await slot.Client.CallAsync("scene",payload.Value,_options.SolveTimeout,token,bytes=>bytes,_replies,replyLimit).ConfigureAwait(false);
                    slot.Client.SceneRevision=identity.SceneRevision;
                }
                AttemptingView?.Invoke(index,cap,isolatedRetry);
                progress?.Report(new(JobPhase.Drawing,Volatile.Read(ref done),views.Count){Workers=plan.Count});
                slot.Client.NativeStarted=()=>SolvingView?.Invoke(slot.Client.ProcessId,index);
                slot.Client.Progress=progress is null?null:new ProgressSink(p=>
                {
                    // One view's inner step at a time: the view shown keeps the status until it finishes or goes quiet,
                    // so parallel workers don't alternate the footer. The host owns the view counts and completion.
                    var now=Environment.TickCount64;
                    lock(shownSync)
                    {
                        if(shown!=index && shown>=0 && results[shown] is null && now-shownAt<1500)return;
                        shown=index;shownAt=now;
                    }
                    progress.Report(new(JobPhase.Drawing,Volatile.Read(ref done),views.Count){Workers=plan.Count,View=views[index].Name,Step=p.Step,StepFraction=p.StepFraction});
                });
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var drawing=await slot.Client.CallAsync("solve",EncodedRequest?.Invoke(index)??WorkerCodec.Encode(WorkerCodec.Request.FromSettings(identity,index,views[index],settings)),_options.SolveTimeout,token,bytes=>
                {
                    var reply=WorkerCodec.Decode<WorkerCodec.Reply>(bytes);
                    reply=ObserveReply?.Invoke(reply)??reply;
                    if(reply.Identity!=identity || reply.ViewIndex!=index)throw new InvalidDataException("Stale worker reply rejected (generation/revisions/view).");
                    token.ThrowIfCancellationRequested();
                    if(EncodedDrawing is not {} encoded)return WorkerCodec.RestoreDrawing(reply.Drawing);
                    encoded(index,reply.Drawing);
                    using var document=System.Text.Json.JsonDocument.Parse(reply.Drawing);var values=document.RootElement.GetProperty("Values");
                    return new ViewDrawing{Name=values.GetProperty("Name").GetString()!,Error=values.GetProperty("Error").GetString()};
                },_replies,replyLimit).ConfigureAwait(false);
                seconds[index]=clock.Elapsed.TotalSeconds;
                drawing._diagnostics.Add($"Worker: isolated whole-view solve; matching runtime and engine; scene retained per process. Reservation={plan.Count} x {plan.CapBytes/(double)WorkerMemoryPolicy.GiB:F1} GiB + host reply {plan.HostReplyBytes/(double)WorkerMemoryPolicy.GiB:F1} GiB; one receive at a time; budget={plan.BudgetBytes/(double)WorkerMemoryPolicy.GiB:F1} GiB.");
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
    private sealed class ProgressSink(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }
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
