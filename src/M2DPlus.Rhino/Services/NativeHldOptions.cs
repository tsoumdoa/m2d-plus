using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Only native HLD changes in opt-in fast mode. Exact kernels and audit budgets are untouched.</summary>
internal readonly record struct NativeHldOptions(bool MultipleThreads, bool IncludeHiddenCurves, bool Fast)
{
    internal static NativeHldOptions For(ViewRequest view, NativeSettings settings) => view.Settings?.Engine == EngineMode.Fast
        ? new(true, view.Settings.RequestedHidden, true) : new(settings.MultipleThreads, true, false);

    internal void Begin(ViewDrawing output, IEnumerable<VisibilityInput> inputs, NativeSettings settings)
    {
        output.MultipleThreads |= MultipleThreads;
        output._diagnostics.Add($"Native HLD options: multipleThreads={MultipleThreads}; IncludeHiddenCurves={IncludeHiddenCurves}.");
        if (!output.Diagnostics.Contains(ContactNote, StringComparer.Ordinal)) output._diagnostics.Add(ContactNote);
        if (Fast)
        {
            // A fallback sees the full registrations, a superset of an attempted
            // composed solve. Do not double-count sources if composition failed.
            output.FastNativeSources = Math.Max(output.FastNativeSources, inputs.Select(i => i.Source.Identity).Distinct(StringComparer.Ordinal).Count());
            if (!IncludeHiddenCurves && !output.Diagnostics.Contains(HiddenAuditNote, StringComparer.Ordinal))
                output._diagnostics.Add(HiddenAuditNote);
        }
        settings.ObserveHldOptions?.Invoke(this);
    }

    private const string HiddenAuditNote = "Native hidden curves not requested: false-Hidden checking skipped for uncomputed hidden curves; any residual native Hidden fragments are still audited. Verifier budgets are unchanged.";
    private const string ContactNote = "Native contacts retain native tolerance ties; joints suppresses only certified planar flush contacts. Native/freeform contact classification is not certified; shared native edges retain native duplicate ownership where supplied.";
}
