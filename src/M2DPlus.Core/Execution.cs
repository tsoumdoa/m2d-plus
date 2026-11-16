namespace M2DPlus.Core;

public readonly record struct RequestIdentity(string SceneRevision, string ViewRevision, string SettingsRevision, long Generation);
/// <summary>Job stages (UX §5.2). Append only. <see cref="Queued"/> means waiting for the global scheduler (another
/// drawing holds it); <see cref="Output"/> is building the presented output after the solve.</summary>
public enum JobPhase { Reading, Preparing, Queued, Drawing, Complete, Cancelled, Failed, Output }

/// <summary>One progress report. <paramref name="Done"/> / <paramref name="Total"/> count objects (Reading, Preparing)
/// or views (Drawing). The optional details say what is being processed: the view, its step ("hidden lines") and that
/// step's fraction, the number of workers, and how many jobs are ahead in the queue.</summary>
public readonly record struct JobProgress(JobPhase Phase, int Done, int Total)
{
    public string? View { get; init; }
    public string? Step { get; init; }
    public double? StepFraction { get; init; }
    public int Workers { get; init; }
    public int? Ahead { get; init; }
}

/// <summary>Cancellation rejects publication immediately; it does not release native resources.</summary>
public sealed class RequestGate : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancel;
    private RequestIdentity _current;
    private long _generation;
    private bool _disposed;
    public (RequestIdentity Identity, CancellationToken Token) Begin(string scene, string views, string settings)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelCore();
            _cancel = new();
            _current = new(scene, views, settings, checked(++_generation));
            return (_current, _cancel.Token);
        }
    }
    public bool IsCurrent(RequestIdentity identity)
    {
        lock (_sync) return !_disposed && _cancel is { IsCancellationRequested: false } && identity == _current;
    }
    // Run publication while holding the gate: Begin/Cancel cannot interleave with publication.
    public bool TryPublish(RequestIdentity identity, bool complete, Action publish)
        => complete && TryUpdate(identity, publish);
    public bool TryUpdate(RequestIdentity identity, Action update)
    {
        lock (_sync)
        {
            if (!IsCurrent(identity)) return false;
            update();
            return true;
        }
    }
    public void Cancel() { lock (_sync) CancelCore(); }
    private void CancelCore() { _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; }
    public void Dispose() { lock (_sync) { CancelCore(); _disposed = true; } }
}

// Append only: the M1 identities remain unchanged.
/// <summary>Values are stable: append only. <see cref="Ignored"/> holds the drawn lines of objects the
/// <c>min visible</c> rule ignores (seen only through small gaps); they keep nothing else.</summary>
public enum LineClass { Visible, Hidden, Unresolved, Beyond, FarClip, Cut, Hatch, Profile, Ignored }
public sealed record QualityReport(
    string Route, int UncertainCount, int InvalidExtractionCount,
    IReadOnlyDictionary<string, int> UnsupportedByType, IReadOnlyList<string> Normalisation,
    bool Quarantined, string ValidationDomain = "Native uncut parallel; analytic baseline properties only; general freeform visibility unknown; sections not evaluated (M2)");

/// <summary>One native slot, process wide. A token cancels waiting/cooperative work, never releases a live call.</summary>
public sealed class NativeScheduler
{
    public static NativeScheduler Global { get; } = new(512L * 1024 * 1024);
    private readonly SemaphoreSlim _slot = new(1, 1);
    public long BudgetBytes { get; }
    public NativeScheduler(long budgetBytes)
    {
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        BudgetBytes = budgetBytes;
    }
    private int _waiting;
    /// <param name="waiting">Called once, before waiting, when another job holds the slot: the number ahead.</param>
    public async Task<T> RunAsync<T>(long estimatedBytes, Func<CancellationToken, T> native, CancellationToken token, Action<int>? waiting = null)
    {
        if (estimatedBytes < 0 || estimatedBytes > BudgetBytes)
            throw new InvalidOperationException("Worker unavailable: scene exceeds the in-process budget.");
        if (!_slot.Wait(0))
        {
            // Another drawing holds the slot: say so, with how many are ahead (the running one plus earlier waiters).
            var ahead = Interlocked.Increment(ref _waiting);
            try { waiting?.Invoke(ahead); await _slot.WaitAsync(token).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => native(token), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _slot.Release(); }
    }
}
