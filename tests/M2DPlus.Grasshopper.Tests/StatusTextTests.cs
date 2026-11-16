using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>The footer status line for every job stage (owner feedback on beta.1, UX §5.2) and how it fits layout B's
/// fixed width.</summary>
public sealed class StatusTextTests
{
    [Fact]
    public void EveryStageSaysWhatIsBeingProcessed()
    {
        Assert.Equal("Loading objects 2,400 / 6,000", StatusText.Of(new(JobPhase.Reading, 2400, 6000)).Full);
        Assert.Equal("Loading objects 120…", StatusText.Of(new(JobPhase.Reading, 120, 0)).Full);
        Assert.Equal("Waiting for another drawing (2 ahead)…", StatusText.Of(new(JobPhase.Queued, 0, 8) { Ahead = 2 }).Full);
        Assert.Equal("Waiting for another drawing…", StatusText.Of(new(JobPhase.Queued, 0, 8)).Full);
        Assert.Equal("Preparing geometry 3,100 / 6,000", StatusText.Of(new(JobPhase.Preparing, 3100, 6000)).Full);
        Assert.Equal("Drawing 3 / 8 · Section A · hidden lines 45 %",
            StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.HiddenLines, StepFraction = .456 }).Full);
        Assert.Equal("Drawing 3 / 8 · Section A · sections", StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.Sections }).Full);
        Assert.Equal("Drawing 5 / 8 on 3 workers", StatusText.Of(new(JobPhase.Drawing, 5, 8) { Workers = 3 }).Full);
        Assert.Equal("Drawing 0 / 8 on 3 workers · sending scene", StatusText.Of(new(JobPhase.Drawing, 0, 8) { Workers = 3, Step = "sending scene" }).Full);
        Assert.Equal("Building output…", StatusText.Of(new(JobPhase.Output, 0, 0)).Full);
        Assert.Equal("Drawing 8 / 8", StatusText.Of(new(JobPhase.Drawing, 8, 8)).Core);
    }

    [Fact]
    public void TheEstimateIsAppendedWhenKnown()
    {
        Assert.Equal("Drawing 3 / 8 · Section A · hidden lines 45 % · ~40 s left",
            StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.HiddenLines, StepFraction = .45 }, 41).Full);
        Assert.Null(StatusText.Of(new(JobPhase.Drawing, 2, 8)).Eta);
    }

    /// <summary>Six units per character stands in for the font.</summary>
    private static float Measure(string text) => text.Length * 6;

    [Fact]
    public void FittingDropsDetailThenViewThenEstimate()
    {
        var line = StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.HiddenLines, StepFraction = .45 }, 41);
        Assert.Equal(line.Full, line.Fit(Measure, 1000));
        Assert.Equal("Drawing 3 / 8 · Section A · ~40 s left", line.Fit(Measure, Measure("Drawing 3 / 8 · Section A · ~40 s left")));
        Assert.Equal("Drawing 3 / 8 · ~40 s left", line.Fit(Measure, Measure("Drawing 3 / 8 · ~40 s left") + 5));
        // Only the core fits whole: as much of the view as fits, ending in an ellipsis.
        Assert.Equal("Drawing 3 / 8 · Sectio…", line.Fit(Measure, Measure("Drawing 3 / 8 · Sectio…") + 3));
        Assert.Equal("Drawing 3 / 8", line.Fit(Measure, Measure("Drawing 3 / 8") + 1));
        // Even the core doesn't fit: the core is returned, and the footer ends it with an ellipsis.
        Assert.Equal("Drawing 3 / 8", line.Fit(Measure, 20));
        // Workers: no view, so the step is what gets shortened.
        var workers = StatusText.Of(new(JobPhase.Drawing, 0, 8) { Workers = 3, Step = "sending scene" });
        Assert.Equal("Drawing 0 / 8 on 3 workers · sendi…", workers.Fit(Measure, Measure("Drawing 0 / 8 on 3 workers · sendi…")));
        // A short line is never shortened.
        var plain = StatusText.Of(new(JobPhase.Output, 0, 0));
        Assert.Equal("Building output…", plain.Fit(Measure, 200));
    }

    [Fact]
    public void ConvertToViewBoxesSummary()
    {
        Assert.Equal("Converted 3 to view boxes; refused 1: Floor: not planar.",
            ViewCommands.ConvertSummary(3, 0, ["Floor: not planar"]));
        Assert.Equal("Converted 1 to view box; 2 already were.", ViewCommands.ConvertSummary(1, 2, []));
        Assert.Equal("Converted 0 to view boxes; refused 3: A: not planar; B: its outline has 3 corners, not 4….",
            ViewCommands.ConvertSummary(0, 0, ["A: not planar", "B: its outline has 3 corners, not 4", "C: the curve is not closed"]));
    }
}
