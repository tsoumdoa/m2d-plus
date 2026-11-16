using System.Globalization;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>
/// One status line in parts, so the fixed-width footer (layout B) can drop the least important parts first: the step
/// detail, then the view name, then the estimate, and only then end the core text in an ellipsis. The tooltip and
/// Info show <see cref="Full"/>.
/// </summary>
/// <param name="Core">Always shown: "Drawing 3 / 8", "Loading objects 2,400 / 6,000".</param>
/// <param name="View">The view being drawn, if one.</param>
/// <param name="Detail">The step within it: "hidden lines 45 %".</param>
/// <param name="Eta">"~40 s left", when there is enough history.</param>
public sealed record StatusLine(string Core, string? View = null, string? Detail = null, string? Eta = null)
{
    public string Full => Join(Core, View, Detail, Eta);
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The longest form that fits <paramref name="width"/> by <paramref name="measure"/>: whole parts first
    /// (dropping the step detail, then the view, then the estimate), else the core and as much of the view (or the step)
    /// as fits, ending in an ellipsis. The last resort is the core alone, which the caller ends with an ellipsis if even
    /// that is too wide.</summary>
    public string Fit(Func<string, float> measure, float width)
    {
        if (measure(Full) <= width) return Full;
        // Shorter whole forms, each only when it still says more than the core.
        var shorter = new List<string>();
        if (Detail is not null && View is not null) shorter.Add(Join(Core, View, Eta));
        if (Eta is not null && (View ?? Detail) is not null) shorter.Add(Join(Core, Eta));
        foreach (var candidate in shorter)
            if (measure(candidate) <= width) return candidate;
        if ((View ?? Detail) is { } extra)
            for (var length = extra.Length - 1; length >= 3; length--)
            {
                var candidate = Core + " · " + extra[..length].TrimEnd() + "…";
                if (measure(candidate) <= width) return candidate;
            }
        return Core;
    }
}

/// <summary>Status lines for every job stage (owner feedback on beta.1, UX §5.2). Invariant number formatting.</summary>
public static class StatusText
{
    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static StatusLine Of(JobProgress progress, double? remainingSeconds = null)
    {
        var eta = remainingSeconds is double s ? JobProgressModel.Eta(s) : null;
        switch (progress.Phase)
        {
            case JobPhase.Reading:
                return new(progress.Total > 0 ? $"Loading objects {N(progress.Done)} / {N(progress.Total)}" : $"Loading objects {N(progress.Done)}…", Eta: eta);
            case JobPhase.Queued:
                return new(progress.Ahead is int ahead and > 0 ? $"Waiting for another drawing ({N(ahead)} ahead)…" : "Waiting for another drawing…", Eta: eta);
            case JobPhase.Preparing:
                return new(progress.Total > 0 ? $"Preparing geometry {N(progress.Done)} / {N(progress.Total)}" : "Preparing geometry…", Eta: eta);
            case JobPhase.Drawing when progress.Workers > 0:
                return new($"Drawing {N(Math.Min(progress.Done, progress.Total))} / {N(progress.Total)} on {N(progress.Workers)} worker{(progress.Workers == 1 ? "" : "s")}",
                    Detail: progress.Step, Eta: eta);
            case JobPhase.Drawing:
            {
                var current = Math.Min(progress.Done + 1, Math.Max(1, progress.Total));
                var detail = progress.Step is { } step
                    ? progress.StepFraction is double f ? $"{step} {(int)Math.Floor(Math.Clamp(f, 0, 1) * 100)} %" : step
                    : null;
                return new($"Drawing {N(current)} / {N(progress.Total)}", progress.View, detail, eta);
            }
            case JobPhase.Output:
                return new("Building output…", Eta: eta);
            case JobPhase.Complete:
                return new("Building output…");
            default:
                return new(progress.Phase.ToString());
        }
    }
}
