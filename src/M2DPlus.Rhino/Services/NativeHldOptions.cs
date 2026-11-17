using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Native API flags. Exact fallback/composition stays serial and hidden-on by default.</summary>
internal readonly record struct NativeHldOptions(bool MultipleThreads, bool IncludeHiddenCurves)
{
    internal static NativeHldOptions For(ViewRequest view, NativeSettings settings) => new(settings.MultipleThreads, true);

    internal void Begin(ViewDrawing output, IEnumerable<VisibilityInput> inputs, NativeSettings settings)
    {
        output.MultipleThreads |= MultipleThreads;
        output._diagnostics.Add($"Native HLD options: multipleThreads={MultipleThreads}; IncludeHiddenCurves={IncludeHiddenCurves}.");
        if (output.Settings?.Engine == EngineMode.Native)
            output.FastNativeSources = inputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).Count();
        else if (!output.Diagnostics.Contains(ContactNote, StringComparer.Ordinal)) output._diagnostics.Add(ContactNote);
        settings.ObserveHldOptions?.Invoke(this);
    }
    private const string ContactNote = "Native contacts retain native tolerance ties; joints suppresses only certified planar flush contacts. Native/freeform contact classification is not certified; shared native edges retain native duplicate ownership where supplied.";
}
