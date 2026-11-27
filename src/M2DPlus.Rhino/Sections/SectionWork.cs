namespace M2DPlus.Rhino.Sections;

/// <summary>Observations only; no elapsed-time or data-dependent changes to certificate budgets.</summary>
internal sealed class SectionWork : IDisposable
{
    private static readonly AsyncLocal<SectionWork?> Current = new();
    private readonly SectionWork? _previous = Current.Value;
    private long _patches, _coverage, _slabHits, _coverageHits, _guards;
    private long _guardPassed, _guardRefused, _guardUnresolved, _fallbackAdmits, _fallbackRefusals;
    private double _guardSeconds;
    internal SectionWork() => Current.Value = this;
    internal static void Slab(int patches, bool hit)
    {
        if (Current.Value is not { } work) return;
        Interlocked.Add(ref work._patches, patches);
        if (hit) Interlocked.Increment(ref work._slabHits);
    }
    internal static void Coverage(int nodes, bool hit)
    {
        if (Current.Value is not { } work) return;
        Interlocked.Add(ref work._coverage, nodes);
        if (hit) Interlocked.Increment(ref work._coverageHits);
    }
    internal static void Guard(double seconds, string outcome)
    {
        if (Current.Value is not { } work) return;
        work._guards++; work._guardSeconds += seconds; // Validation is serial.
        if (outcome == "passed") work._guardPassed++;
        else if (outcome == "refused") work._guardRefused++;
        else work._guardUnresolved++;
    }
    internal static void GuardFallback(bool admitted)
    {
        if (Current.Value is not { } work) return;
        if (admitted) work._fallbackAdmits++; else work._fallbackRefusals++;
    }
    internal long CoverageNodes => _coverage;
    internal string Diagnostic => $"Section work: slab patches={_patches}; slab budget hits={_slabHits}; shared-topology nodes={_coverage}; shared-topology budget hits={_coverageHits}; overlap guard calls={_guards}; overlap guard seconds={_guardSeconds:F6}; guard passed={_guardPassed}; guard refused={_guardRefused}; guard unresolved={_guardUnresolved}; fallback admits={_fallbackAdmits}; fallback refusals={_fallbackRefusals}.";
    public void Dispose() => Current.Value = _previous;
}
