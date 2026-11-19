namespace M2DPlus.Rhino.Services;

/// <summary>Requested HLD flags; production defaults to threaded computation.</summary>
internal readonly record struct NativeHldOptions(bool MultipleThreads, bool IncludeHiddenCurves)
{
    internal static NativeHldOptions For(ViewRequest view, NativeSettings settings) => new(settings.MultipleThreads, view.Settings?.Hidden ?? true);
    internal void Begin(ViewDrawing output, IEnumerable<VisibilityInput> inputs, NativeSettings settings)
    {
        output.MultipleThreads = MultipleThreads;
        output.FastNativeSources = inputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).Count();
        output._diagnostics.Add($"HLD flags: multithreaded={MultipleThreads}; hidden={IncludeHiddenCurves}; tangents={output.Settings?.Tangents ?? false}; seams={output.Settings?.Seams ?? false}.");
        settings.ObserveHldOptions?.Invoke(this);
    }
}
