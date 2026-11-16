using System.Diagnostics;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Per-solve progress, inherited by parallel managed work. No process-global view state.</summary>
internal sealed class DrawingProgress : IDisposable
{
    private static readonly AsyncLocal<DrawingProgress?> Current = new();
    private readonly DrawingProgress? _previous = Current.Value;
    private readonly IProgress<JobProgress>? _sink;
    private readonly object _gate = new();
    private readonly int _view, _views;
    private long _last;
    private string _phase = "Drawing";
    private int _done, _total;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Timer? _heartbeat;
    private bool _disposed;
    internal DrawingProgress(IProgress<JobProgress>? sink, int view, int views)
    {
        _sink = sink; _view = view; _views = views; Current.Value = this;
        if (sink is not null) _heartbeat = new Timer(_ => { lock (_gate) Publish(); },null,1000,1000);
    }
    internal static void Phase(string phase)
    {
        if (Current.Value is not { } current) return;
        lock (current._gate)
        {
            current._phase = phase switch
            {
                "normalisation" or "planar extraction" or "composition setup" or "native setup" => "Preparing geometry",
                "kernel solve" or "curve-plane events" => "Checking visibility",
                "native solve" => "Rhino hidden lines",
                "native ray verification" => "Checking native lines",
                "sections" or "route C trim" => "Building sections",
                _ => "Building drawing"
            };
            current._done = current._total = 0;
            current.Publish();
        }
    }
    internal static void Work(int done, int total)
    {
        if (Current.Value is not { } current) return;
        lock (current._gate) { current._done = done; current._total = total; current.Publish(); }
    }
    internal static IProgress<double> Native { get; } = new NativeSink();
    private sealed class NativeSink : IProgress<double>
    { public void Report(double value) { if (double.IsFinite(value)) Work((int)(Math.Clamp(value,0,1)*100),100); } }
    private void Publish()
    {
        if (_sink is null || _disposed) return;
        long now = Stopwatch.GetTimestamp();
        // Repeated event/output subphases must not flood the UI or the pipe either.
        if (Stopwatch.GetElapsedTime(_last,now).TotalMilliseconds < 100) return;
        _last = now;
        string detail = _total > 0 ? $"{_phase} {_done}/{_total}" : _phase;
        double elapsed = Stopwatch.GetElapsedTime(_started,now).TotalSeconds;
        if (elapsed >= 10) detail += $" · {elapsed:F0}s";
        _sink.Report(new(JobPhase.Drawing,_view,_views) { Detail = detail });
    }
    public void Dispose() { lock (_gate) _disposed = true; _heartbeat?.Dispose(); Current.Value = _previous; }
}

