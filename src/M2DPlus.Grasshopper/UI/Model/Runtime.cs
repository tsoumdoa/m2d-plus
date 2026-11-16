using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Job state as the canvas needs it (UX §5.2). The component reports it; the UI never reads job internals.</summary>
public enum DrawingPhase { Idle, Running, Done, Paused, Cancelled, Error }

/// <summary>Fraction is 0–1 for the progress bar (never decreasing during a job), or null when nothing is known.
/// <see cref="Indeterminate"/>: the current stage has no sub-progress, so the bar shimmers between Fraction and
/// <see cref="SpanEnd"/>. <see cref="Status"/> is the footer's status line while a job runs.</summary>
public readonly record struct DrawingRuntime(DrawingPhase Phase, double? Fraction = null)
{
    public StatusLine? Status { get; init; }
    public bool Indeterminate { get; init; }
    public double SpanEnd { get; init; }
    public static DrawingRuntime Idle { get; } = new(DrawingPhase.Idle);
}

public enum FooterButton { None, Bake, Cancel, Run }

public static class RuntimeModel
{
    /// <summary>The message-tab button: Bake when done, Cancel while running, Run when paused or cancelled (UX §1, §5).</summary>
    public static FooterButton Button(DrawingRuntime runtime) => runtime.Phase switch
    {
        DrawingPhase.Running => FooterButton.Cancel,
        DrawingPhase.Done => FooterButton.Bake,
        DrawingPhase.Paused or DrawingPhase.Cancelled => FooterButton.Run,
        _ => FooterButton.None
    };

    public static string Label(FooterButton button) => button switch
    {
        FooterButton.Bake => "Bake", FooterButton.Cancel => "Cancel", FooterButton.Run => "Run", _ => ""
    };

    public static string Tooltip(FooterButton button) => button switch
    {
        FooterButton.Bake => "Bake every drawing to M2D+ layers, replacing the previous bake.\nShift-click: add as a copy.",
        FooterButton.Cancel => "Cancel the running job. Nothing is published.",
        FooterButton.Run => "Run the drawing now.",
        _ => ""
    };

    /// <summary>Whole-job fraction for the progress line: preparation 0–40 %, drawing 40–100 % (UX §5.2).</summary>
    public static double? Fraction(JobProgress progress)
    {
        var part = progress.Total > 0 ? Math.Clamp((double)progress.Done / progress.Total, 0, 1) : 0;
        return progress.Phase switch
        {
            JobPhase.Reading or JobPhase.Queued => null,
            JobPhase.Preparing => .4 * part,
            JobPhase.Drawing => .4 + .6 * part,
            JobPhase.Complete => 1,
            _ => null
        };
    }
}
