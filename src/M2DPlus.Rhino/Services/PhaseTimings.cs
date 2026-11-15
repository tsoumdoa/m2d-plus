using System.Diagnostics;
namespace M2DPlus.Rhino.Services;

/// <summary>Wall-clock phase observations; no native timing mutex or scheduling changes.</summary>
public sealed class PhaseTimings
{
    private readonly Dictionary<string, double> _seconds = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, double> Seconds => _seconds;
    public void Add(string phase, double seconds) => _seconds[phase] = _seconds.GetValueOrDefault(phase) + seconds;
    public void Include(PhaseTimings other) { foreach (var pair in other.Seconds) Add(pair.Key, pair.Value); }
    public Timeline Start(string phase) => new(this, phase);
    public sealed class Timeline(PhaseTimings owner, string phase) : IDisposable
    {
        private long _start = Stopwatch.GetTimestamp();
        private string _phase = phase;
        private bool _running = true;
        public void Pause() { if (_running) owner.Add(_phase, Stopwatch.GetElapsedTime(_start).TotalSeconds); _running = false; }
        public void Next(string next) { Pause(); _phase = next; _start = Stopwatch.GetTimestamp(); _running = true; }
        public void Dispose() => Pause();
    }
}
