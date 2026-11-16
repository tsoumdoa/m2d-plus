using System.Diagnostics;
using System.IO.Pipes;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

public sealed record WorkerOptions
{
    public int Count { get; init; } = WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount);
    /// <summary>Zero selects the scene/machine policy; positive values override its initial per-worker cap.</summary>
    public long MemoryBytes { get; init; }
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan SolveTimeout { get; init; } = TimeSpan.FromHours(1);
    public string Directory { get; init; } = Path.Combine(Path.GetDirectoryName(typeof(WorkerOptions).Assembly.Location)!, "workers", "net" + Environment.Version.Major);
    public string RhinoSystem { get; init; } = Path.GetDirectoryName(typeof(global::Rhino.RhinoApp).Assembly.Location)!;
    // Resolver is optional: packaged workers resolve the installed Rhino system directly.
    public string? Resolver { get; init; }
    internal string? TestFault { get; init; }
    internal int ProcessorLimit { get; init; } = WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount);
    internal Func<MachineMemory> ReadMemory { get; init; } = WorkerMachineMemory.Read;
    public static WorkerOptions FromEnvironment() => new()
    {
        Count = Number("M2DPLUS_WORKERS",WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount),1,WorkerMemoryPolicy.CpuLimit(Environment.ProcessorCount)),
        MemoryBytes = Environment.GetEnvironmentVariable("M2DPLUS_WORKER_MEMORY_MIB") is null ? 0 : (long)Number("M2DPLUS_WORKER_MEMORY_MIB",0,512,int.MaxValue)*1024*1024,
        Directory = Environment.GetEnvironmentVariable("M2DPLUS_WORKER_DIRECTORY") ?? new WorkerOptions().Directory,
        IdleTimeout = TimeSpan.FromSeconds(Number("M2DPLUS_WORKER_IDLE_SECONDS",600,1,86400)),
        StartupTimeout = TimeSpan.FromSeconds(Number("M2DPLUS_WORKER_STARTUP_SECONDS",120,15,3600)),
        SolveTimeout = TimeSpan.FromSeconds(Number("M2DPLUS_WORKER_SOLVE_SECONDS",3600,1,86400))
    };
    private static int Number(string name,int fallback,int min,int max) => int.TryParse(Environment.GetEnvironmentVariable(name),out var n)?Math.Clamp(n,min,max):fallback;
}

internal sealed class WorkerMemoryException(string message) : IOException(message);

internal sealed class WorkerUnavailableException(string message) : IOException(message);

internal sealed class WorkerClient : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly WorkerJob _job;
    private readonly Process _process;
    private readonly object _sync=new();
    private bool _disposed;
    internal Action? NativeStarted { get; set; }
    internal string? SceneRevision { get; set; }
    internal int ProcessId { get; }
    internal long PeakBytes => _job.PeakBytes;
    internal long MemoryCapBytes { get; private init; }
    internal int ProcessorLimit { get; private init; }
    internal bool MemoryLimitHit => _job.MemoryLimitHit;
    internal long ResidentBytes { get { lock(_sync){if(_disposed)return 0;_process.Refresh();return _process.WorkingSet64;} } }
    private WorkerClient(NamedPipeServerStream pipe,WorkerJob job,Process process) { _pipe=pipe;_job=job;_process=process;ProcessId=process.Id; }
    internal static async Task<WorkerClient> StartAsync(WorkerOptions options, WorkerFingerprint expected, CancellationToken token)
    {
        var executable=Path.Combine(options.Directory,"M2DPlus.Worker.exe");
        if(!File.Exists(executable))throw new WorkerUnavailableException("Worker unavailable: matching .NET " + expected.RuntimeMajor + " executable is missing.");
        var name="m2d-plus-"+Guid.NewGuid().ToString("N");
        var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        WorkerJob? job=null;Process? process=null;WorkerClient? client=null;
        try
        {
            job=new(options.MemoryBytes);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
            start.ArgumentList.Add(name);start.ArgumentList.Add(options.RhinoSystem);
            if(options.Resolver is {} resolver)start.ArgumentList.Add(resolver);
            // A net8 apphost may never roll into net10, or vice versa.
            if(options.TestFault is {} fault)start.Environment["M2DPLUS_WORKER_TEST_FAULT"]=fault;
            else start.Environment.Remove("M2DPLUS_WORKER_TEST_FAULT");
            start.Environment["DOTNET_ROLL_FORWARD"]="LatestPatch";
            // Bound managed kernel parallelism across the whole reservation instead
            // of advertising all host cores independently to every worker.
            start.Environment["DOTNET_PROCESSOR_COUNT"]=options.ProcessorLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment.Remove("M2DPLUS_WORKER_DIRECTORY");
            start.Environment["M2DPLUS_WORKER_BUDGET"]=options.MemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            process=Process.Start(start)??throw new IOException("Worker process did not start.");
            client=new(pipe,job,process){MemoryCapBytes=options.MemoryBytes,ProcessorLimit=options.ProcessorLimit};
            job.Assign(process);
            process.PriorityClass=ProcessPriorityClass.BelowNormal;
            // Bootstrap waits on this line before loading any Rhino assembly/native code.
            await process.StandardInput.WriteLineAsync("contained").ConfigureAwait(false);process.StandardInput.Close();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(options.StartupTimeout);
            using var kill=timeout.Token.Register(client.Kill);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var actual=WorkerCodec.Decode<WorkerFingerprint>(await WorkerFrames.ReadAsync(pipe,timeout.Token,16*1024).ConfigureAwait(false));
            if(!expected.Accepts(actual))throw new WorkerUnavailableException("Worker unavailable: runtime, architecture, Rhino or engine/schema fingerprint mismatch.");
            await WorkerFrames.WriteAsync(pipe,WorkerCodec.Encode(expected),timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch(Exception ex)
        {
            if(client is not null)client.Dispose();else {pipe.Dispose();job?.Dispose();if(process is not null){if(!process.HasExited)process.Kill(true);process.Dispose();}}
            token.ThrowIfCancellationRequested();
            if(ex is WorkerUnavailableException)throw;
            throw new WorkerUnavailableException("Worker unavailable: startup/handshake failed: "+ex.Message);
        }
    }
    internal Task<byte[]> CallAsync(string kind,byte[] payload,TimeSpan timeout,CancellationToken token) =>
        CallAsync(kind,payload,timeout,token,bytes=>bytes,null,WorkerCompression.MaximumExpandedBytes);
    internal async Task<T> CallAsync<T>(string kind,byte[] payload,TimeSpan timeout,CancellationToken token,
        Func<byte[],T> consume,SemaphoreSlim? receiveGate,long expandedLimit)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        using var kill=deadline.Token.Register(Kill);
        try
        {
            await WorkerFrames.WriteAsync(_pipe,WorkerPackets.Encode(kind,payload),deadline.Token).ConfigureAwait(false);
            while(true)
            {
                // Wait for a ready frame before taking receive memory. In particular,
                // a slow native solve must not block a sibling's scene acknowledgement.
                var length=await WorkerFrames.ReadLengthAsync(_pipe,deadline.Token).ConfigureAwait(false);
                if(receiveGate is not null)await receiveGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    var reply=await WorkerPackets.ReadFrameAsync(_pipe,length,expandedLimit,deadline.Token).ConfigureAwait(false);
                    if(reply.Kind=="started"){NativeStarted?.Invoke();continue;}
                    if(reply.Kind=="progress"){Progress?.Report(WorkerCodec.Decode<JobProgress>(reply.Payload));continue;}
                    if(reply.Kind=="memory")throw new WorkerMemoryException(System.Text.Encoding.UTF8.GetString(reply.Payload));
                    if(reply.Kind=="error")throw new IOException(System.Text.Encoding.UTF8.GetString(reply.Payload));
                    if(reply.Kind!=kind)throw new InvalidDataException("Unexpected worker response.");
                    return consume(reply.Payload);
                }
                finally{receiveGate?.Release();}
            }
        }
        catch(Exception ex)
        {
            Kill();token.ThrowIfCancellationRequested();
            // A worker's own deadline is a per-view failure. Preserve caller
            // cancellation separately so a timeout cannot discard sibling views.
            if(deadline.IsCancellationRequested)throw new TimeoutException($"Worker {kind} exceeded its {timeout.TotalSeconds:g} second deadline.",ex);
            throw;
        }
    }
    internal void Kill() { lock(_sync){if(!_disposed)_job.Kill();} }
    /// <summary>The current call's inner progress from the child (its step and fraction), for the host's status.</summary>
    internal IProgress<JobProgress>? Progress { get; set; }
    public void Dispose()
    {
        lock(_sync)
        {
            if(_disposed)return;_job.Kill();
            // Death, not cancellation, releases this slot. Never start its replacement while native is alive.
            if(!_process.WaitForExit(10000))throw new IOException("Owned worker did not exit after job termination.");
            _pipe.Dispose();_job.Dispose();_process.Dispose();_disposed=true;
        }
    }
}
