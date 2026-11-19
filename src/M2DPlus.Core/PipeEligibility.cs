namespace M2DPlus.Core;

/// <summary>Conservative admission for the local surface-visibility experiment. Null means eligible.</summary>
public static class PipeEligibility
{
    // Only a strict enclosure separation is a shortcut. Touching/invalid ranges
    // need geometry-level inspection and must never be promoted to no-cut.
    public static int PlaneSide(double minimum, double maximum, double margin)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum ||
            !double.IsFinite(margin) || margin < 0) return 0;
        return minimum > margin ? 1 : maximum < -margin ? -1 : 0;
    }

    public static string? Reason(bool millimetres, bool parallel, bool section, bool clip,
        bool blocks, string? unsupported)
    {
        if (!parallel) return "view is not parallel";
        if (section) return "view has a section";
        if (clip) return "view has a clip";
        if (!millimetres) return "model units are not millimetres";
        if (blocks) return "scene has block instances";
        return unsupported is null ? null : "scene has " + unsupported;
    }
}
