namespace M2DPlus.Core;

/// <summary>How long each stage of a job took (seconds): the weights of the next run of the same scene and views.</summary>
public sealed record StageTimes(double Loading, double Preparing, double Drawing, double Output)
{
    public double Total => Loading + Preparing + Drawing + Output;
    /// <summary>Shares used without history: loading and preparing are quick next to drawing.</summary>
    public static StageTimes Default { get; } = new(.15, .10, .70, .05);
}

/// <summary>Where a job is, for the progress bar (UX §5.2).</summary>
/// <param name="Fraction">0–1, never decreasing during one job.</param>
/// <param name="Indeterminate">The current stage has no reliable sub-progress: show a shimmer between
/// <paramref name="Fraction"/> and <paramref name="SpanEnd"/> rather than a frozen bar.</param>
/// <param name="SpanEnd">The end of the current unit of work (the stage, or the view being drawn).</param>
/// <param name="RemainingSeconds">A rough estimate, only when there is enough history; otherwise null.</param>
public readonly record struct ProgressState(double Fraction, bool Indeterminate, double SpanEnd, double? RemainingSeconds);

/// <summary>
/// The stage and progress model of one job (owner feedback on beta.1: "Queued" was vague). Stages are loading objects,
/// waiting for another drawing, preparing geometry, drawing views, building output. Each stage gets a share of the bar:
/// the time it took in the previous run of the same scene and views when known, else <see cref="StageTimes.Default"/>.
/// The fraction only moves forward. Waiting and stages without a count are indeterminate. Plain values only.
/// </summary>
public sealed class JobProgressModel
{
    private readonly double[] _start = new double[4], _weight = new double[4];
    private double _fraction;
    /// <summary>The weights come from a previous run of the same scene and views.</summary>
    public bool HasHistory { get; }

    /// <param name="history">The previous run's stage times, or null.</param>
    /// <param name="loading">False when the captured scene is reused: loading takes no share.</param>
    public JobProgressModel(StageTimes? history, bool loading = true)
    {
        HasHistory = history is { Total: > 0 };
        var times = HasHistory ? history! : StageTimes.Default;
        // A floor keeps a stage that was instant last time visible; loading has no share when it won't run.
        var raw = new[] { loading ? times.Loading : 0, times.Preparing, times.Drawing, times.Output }
            .Select((t, i) => i == 0 && !loading ? 0 : Math.Max(t, times.Total * .02)).ToArray();
        var total = raw.Sum();
        for (var i = 0; i < 4; i++)
        {
            _weight[i] = raw[i] / total;
            _start[i] = i == 0 ? 0 : _start[i - 1] + _weight[i - 1];
        }
    }

    private static int Stage(JobPhase phase) => phase switch
    {
        JobPhase.Reading => 0, JobPhase.Queued or JobPhase.Preparing => 1, JobPhase.Drawing => 2, JobPhase.Output => 3, _ => 4,
    };

    /// <summary>Within a view: where each step usually starts, so a view's own progress moves forward too.</summary>
    private static double StepStart(string? step) => step switch
    {
        ViewSteps.ExactLines => .05, ViewSteps.HiddenLines or ViewSteps.Fast => .2, ViewSteps.Sections => .75, ViewSteps.Checking or ViewSteps.ExactCurved => .85, ViewSteps.Output => .92, _ => 0,
    };
    private static double StepEnd(string? step) => step switch
    {
        ViewSteps.ExactLines => .2, ViewSteps.HiddenLines or ViewSteps.Fast => .75, ViewSteps.Sections => .85, ViewSteps.Checking or ViewSteps.ExactCurved => .92, ViewSteps.Output => 1, _ => .05,
    };

    /// <summary>Folds one report in; <paramref name="elapsedSeconds"/> is the job's running time.</summary>
    public ProgressState Update(JobProgress report, double elapsedSeconds)
    {
        var stage = Stage(report.Phase);
        if (stage > 3) { _fraction = report.Phase == JobPhase.Complete ? 1 : _fraction; return new(_fraction, false, _fraction, null); }
        double sub; bool indeterminate; double spanEnd;
        var start = _start[stage]; var weight = _weight[stage];
        switch (report.Phase)
        {
            case JobPhase.Queued:
                sub = 0; indeterminate = true; spanEnd = start;
                break;
            case JobPhase.Drawing when report.Total > 0:
            {
                var view = Math.Clamp(report.Done, 0, report.Total);
                // Parallel workers count whole views. In process, or with one worker (its one view in flight forwards its
                // steps), the step and that step's own fraction (hidden lines, exact lines, checking) move within the view.
                var whole = report.Workers > 1;
                var within = whole || view == report.Total ? 0
                    : StepStart(report.Step) + (StepEnd(report.Step) - StepStart(report.Step)) * Math.Clamp(report.StepFraction ?? 0, 0, 1);
                sub = (view + within) / report.Total;
                indeterminate = whole || report.StepFraction is null;
                spanEnd = start + weight * Math.Min(1, (view + (report.Workers > 0 ? Math.Min(report.Workers, report.Total - view) : 1d)) / report.Total);
                break;
            }
            default:
                indeterminate = report.Total <= 0;
                sub = report.Total > 0 ? Math.Clamp((double)report.Done / report.Total, 0, 1) : 0;
                spanEnd = start + weight;
                break;
        }
        _fraction = Math.Max(_fraction, Math.Clamp(start + weight * sub, 0, 1));
        spanEnd = Math.Clamp(Math.Max(spanEnd, _fraction), 0, 1);
        return new(_fraction, indeterminate, spanEnd, Remaining(elapsedSeconds));
    }

    /// <summary>A linear estimate from the time so far: with history from 5 % on (the weights are then time shares),
    /// without it only from 25 % and 4 s, and never under 2 s.</summary>
    private double? Remaining(double elapsed)
    {
        var enough = HasHistory ? _fraction >= .05 && elapsed >= 1 : _fraction >= .25 && elapsed >= 4;
        if (!enough || _fraction >= 1) return null;
        var remaining = elapsed * (1 - _fraction) / _fraction;
        return remaining < 2 ? null : remaining;
    }

    /// <summary>"~40 s left", "~2 min left": seconds to the nearest 5 above 20 s, minutes above 90 s.</summary>
    public static string Eta(double seconds) => seconds switch
    {
        < 20 => $"~{Math.Max(1, (int)Math.Round(seconds))} s left",
        < 90 => $"~{(int)(Math.Round(seconds / 5) * 5)} s left",
        _ => $"~{(int)Math.Round(seconds / 60)} min left",
    };
}

/// <summary>The steps of drawing one view, as the status shows them, and how the engine's phase names map to them.</summary>
public static class ViewSteps
{
    public const string ExactLines = "exact lines", HiddenLines = "hidden lines", Fast = "fast", Sections = "sections", Checking = "checking", Output = "output",
        ExactCurved = "exact curved";

    /// <summary>The user-facing step of an engine phase (PhaseTimings names), or null for bookkeeping phases.</summary>
    public static string? Of(string phase) => phase switch
    {
        "planar extraction" or "kernel solve" or "interval union" or "curve-plane events" or "composition setup" => ExactLines,
        "native setup" or "native solve" or "native extraction and duplicates" => HiddenLines,
        "sections" or "section output construction" or "section restyle and output" => Sections,
        "native ray verification" => Checking,
        "exact curved" => ExactCurved,
        "output construction" or "Beyond/fade splitting" or "min visible" => Output,
        _ => null,
    };
}
