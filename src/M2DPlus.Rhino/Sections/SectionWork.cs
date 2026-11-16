namespace M2DPlus.Rhino.Sections;

/// <summary>Observations only; no elapsed-time or data-dependent changes to certificate budgets.</summary>
internal sealed class SectionWork : IDisposable
{
    private static readonly AsyncLocal<SectionWork?> Current = new();
    private readonly SectionWork? _previous = Current.Value;
    private long _patches, _coverage, _slabHits, _coverageHits;
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
    internal string Diagnostic => $"Section work: slab patches={_patches}; slab budget hits={_slabHits}; shared-topology nodes={_coverage}; shared-topology budget hits={_coverageHits}.";
    public void Dispose() => Current.Value = _previous;
}
