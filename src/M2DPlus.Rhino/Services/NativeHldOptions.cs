namespace M2DPlus.Rhino.Services;

/// <summary>Threaded Rhino HLD with the requested hidden flag.</summary>
internal readonly record struct NativeHldOptions(bool MultipleThreads, bool IncludeHiddenCurves)
{
    internal static NativeHldOptions For(ViewRequest view, NativeSettings settings) => new(true, view.Settings?.Hidden ?? true);
    internal void Begin(ViewDrawing output, IEnumerable<VisibilityInput> inputs, NativeSettings settings)
    {
        output.MultipleThreads = true;
        output.FastNativeSources = inputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).Count();
        output._diagnostics.Add($"HLD flags: multithreaded=True; hidden={IncludeHiddenCurves}; tangents={output.Settings?.Tangents ?? false}; seams={output.Settings?.Seams ?? false}.");
        settings.ObserveHldOptions?.Invoke(this);
    }
}
