using System.Globalization;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>
/// A running job's status in the footer's two rows (UX §5.2). Row 1 says <b>what</b> is being processed: the stage with
/// its full counts (<see cref="Core"/>, never shortened) and the view (<see cref="View"/>). Row 2 says <b>how far</b>:
/// the step within it with its percentage (<see cref="Detail"/>) on the left, and the estimate (<see cref="Eta"/>)
/// right-aligned before the Cancel button. All in one font size; when a row is too long the view name is shortened
/// first, then the step, each ending in an ellipsis. The tooltip and Info show <see cref="Full"/>.
/// </summary>
public sealed record StatusLine(string Core, string? View = null, string? Detail = null, string? Eta = null)
{
    public string Full => Join(Core, View, Detail, Eta);
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Row 1 within <paramref name="width"/>: the core, and as much of the view name as fits (at least three
    /// characters before the ellipsis), else the core alone. The core is never shortened.</summary>
    public string Row1(Func<string, float> measure, float width)
    {
        if (View is null) return Core;
        var whole = Join(Core, View);
        return measure(whole) <= width ? whole : Shorten(Core + " · ", View, measure, width) ?? Core;
    }

    /// <summary>Row 2's left part within <paramref name="width"/>: the step, shortened with an ellipsis if needed, or
    /// null when there is no step or no room for three characters of it.</summary>
    public string? Row2(Func<string, float> measure, float width)
    {
        if (Detail is null) return null;
        return measure(Detail) <= width ? Detail : Shorten("", Detail, measure, width);
    }

    private static string? Shorten(string prefix, string text, Func<string, float> measure, float width)
    {
        for (var length = text.Length - 1; length >= 3; length--)
        {
            var candidate = prefix + text[..length].TrimEnd() + "…";
            if (measure(candidate) <= width) return candidate;
        }
        return null;
    }
}

/// <summary>Status lines for every job stage (owner feedback on beta.1, UX §5.2). Invariant number formatting.</summary>
public static class StatusText
{
    /// <summary>Counts in full with thousands separators; from 100,000 on, compactly ("123.5k") so a count always fits
    /// whole: counts are never cut off.</summary>
    public static string Count(int value) => value < 100_000
        ? value.ToString("N0", CultureInfo.InvariantCulture)
        : (value / 1000d).ToString(value < 1_000_000 ? "0.#" : "0", CultureInfo.InvariantCulture) + "k";

    /// <summary>The longest row-1 core the layout reserves width for (UX §1), so every stage's text and counts fit
    /// whole: waiting with two-digit queue, or loading / preparing with five-digit counts.</summary>
    public const string WidestCore = "Waiting for another drawing (99 ahead)";

    /// <summary>Row 2's step: "hidden lines 45 %" when the step reports how far it is, else the step alone.</summary>
    private static string? Step(JobProgress progress) => progress.Step is { } step
        ? progress.StepFraction is double f ? $"{step} {(int)Math.Floor(Math.Clamp(f, 0, 1) * 100)} %" : step
        : null;

    public static StatusLine Of(JobProgress progress, double? remainingSeconds = null)
    {
        var eta = remainingSeconds is double s ? JobProgressModel.Eta(s) : null;
        string Pair(string what) => progress.Total > 0 ? $"{what} {Count(progress.Done)} / {Count(progress.Total)}" : $"{what} {Count(progress.Done)}";
        switch (progress.Phase)
        {
            case JobPhase.Reading:
                return new(Pair("Loading objects"), Detail: Step(progress), Eta: eta);
            case JobPhase.Queued:
                return new(progress.Ahead is int ahead and > 0 ? $"Waiting for another drawing ({Count(ahead)} ahead)" : "Waiting for another drawing", Eta: eta);
            case JobPhase.Preparing:
                return new(progress.Total > 0 ? Pair("Preparing geometry") : "Preparing geometry", Eta: eta);
            case JobPhase.Drawing when progress.Workers > 0:
                // A worker's own step comes with the view it is drawing (one view at a time is shown).
                return new($"Drawing {Count(Math.Min(progress.Done, progress.Total))} / {Count(progress.Total)} · {Count(progress.Workers)} worker{(progress.Workers == 1 ? "" : "s")}",
                    progress.View, Step(progress), eta);
            case JobPhase.Drawing:
            {
                var current = Math.Min(progress.Done + 1, Math.Max(1, progress.Total));
                return new($"Drawing {Count(current)} / {Count(progress.Total)}", progress.View, Step(progress), eta);
            }
            case JobPhase.Output or JobPhase.Complete:
                return new("Building output", Eta: eta);
            default:
                return new(progress.Phase.ToString());
        }
    }
}
