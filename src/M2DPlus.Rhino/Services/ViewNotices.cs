using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Reads a finished view into the plain <see cref="ViewOutcome"/> that <see cref="NoticePolicy"/> judges.
/// A static adapter, so <see cref="ViewDrawing"/> gains no member the worker codec would have to carry.</summary>
public static class ViewNotices
{
    private const string LayerNotFound = "Layer not found '";
    internal const string ZeroDepth = "Zero depth: section-only view; projection skipped";
    internal const string ThinDepth = "Positive depth is within tolerance: no certified projection volume; sections only";
    internal const string CutOnlyEmpty = "Cut only: this view cuts nothing";

    public static ViewOutcome Outcome(ViewDrawing view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var occluding = new Dictionary<string, int>(StringComparer.Ordinal);
        var harmless = new Dictionary<string, int>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (key, count) in view.Report.UnsupportedByType)
        {
            if (!key.StartsWith(PreparedScene.NonOccludingPrefix, StringComparison.Ordinal)) { occluding[key] = count; continue; }
            var type = key[PreparedScene.NonOccludingPrefix.Length..];
            if (type.StartsWith(LayerNotFound, StringComparison.Ordinal)) missing.Add(type[LayerNotFound.Length..].TrimEnd('\''));
            else harmless[type] = count;
        }
        return new(view.Name, view.Error, view.Report.UncertainCount, view.Report.Quarantined, occluding, harmless, missing, view.ToleranceWarning);
    }

    public static IReadOnlyList<Notice> For(ViewDrawing view)
    {
        var notices = NoticePolicy.For(Outcome(view)).ToList();
        if (view.Error is null)
        {
            notices.Add(new(NoticeLevel.Remark, view.Name + ": " + view.EngineSummary));
            if (view.Diagnostics.Any(d => d.StartsWith("min visible ignored", StringComparison.Ordinal)))
                notices.Add(new(NoticeLevel.Remark, view.Name + ": min visible ignored with hidden = off; all objects retained."));
            foreach (var diagnostic in view.Diagnostics.Where(d => d.StartsWith(HldProjection.SkippedSource, StringComparison.Ordinal) ||
                d.StartsWith(HldProjection.FailedClip, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
                notices.Add(new(NoticeLevel.Warning, view.Name + ": " + diagnostic));
            foreach (var diagnostic in view.Diagnostics.Where(d => d.StartsWith("pipes engine:", StringComparison.Ordinal) || d.StartsWith("raster engine:", StringComparison.Ordinal)))
                notices.Add(new(NoticeLevel.Remark, view.Name + ": " + diagnostic));
            foreach (var notice in new[] { ZeroDepth, ThinDepth })
                if (view.Diagnostics.Contains(notice)) notices.Add(new(NoticeLevel.Remark, view.Name + ": " + notice));
            if (view.Diagnostics.Contains(CutOnlyEmpty)) notices.Add(new(NoticeLevel.Remark, view.Name + ": " + CutOnlyEmpty));
        }
        return notices;
    }
}
