using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

/// <summary>The running footer's two rows for every job stage (owner feedback on beta.1, UX §5.2): row 1 says what is
/// being processed, row 2 how far along; counts are never shortened.</summary>
public sealed class StatusTextTests
{
    [Fact]
    public void RowOneSaysWhatWithFullCounts()
    {
        Assert.Equal("Loading objects 2,400 / 6,000", StatusText.Of(new(JobPhase.Reading, 2400, 6000)).Core);
        Assert.Equal("Loading objects 120", StatusText.Of(new(JobPhase.Reading, 120, 0)).Core);
        Assert.Equal("Waiting for another drawing (1 ahead)", StatusText.Of(new(JobPhase.Queued, 0, 8) { Ahead = 1 }).Core);
        Assert.Equal("Waiting for another drawing", StatusText.Of(new(JobPhase.Queued, 0, 8)).Core);
        Assert.Equal("Preparing geometry 3,100 / 6,000", StatusText.Of(new(JobPhase.Preparing, 3100, 6000)).Core);
        var drawing = StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.HiddenLines, StepFraction = .456 }, 41);
        Assert.Equal(("Drawing 3 / 8", "Section A", "hidden lines 45 %", "~40 s left"), (drawing.Core, drawing.View, drawing.Detail, drawing.Eta));
        Assert.Equal("Drawing 3 / 8 · Section A · hidden lines 45 % · ~40 s left", drawing.Full);
        var workers = StatusText.Of(new(JobPhase.Drawing, 5, 8) { Workers = 3, Step = "sending scene" });
        Assert.Equal(("Drawing 5 / 8 · 3 workers", "sending scene"), (workers.Core, workers.Detail));
        // A worker's inner step comes with its view, like in process.
        var child = StatusText.Of(new(JobPhase.Drawing, 5, 8) { Workers = 3, View = "Section A", Step = ViewSteps.ExactLines, StepFraction = .37 });
        Assert.Equal(("Drawing 5 / 8 · 3 workers", "Section A", "exact lines 37 %"), (child.Core, child.View, child.Detail));
        var checking = StatusText.Of(new(JobPhase.Drawing, 0, 1) { View = "Plan", Step = ViewSteps.Checking, StepFraction = .999 });
        Assert.Equal("checking 99 %", checking.Detail);
        Assert.Equal("Building output", StatusText.Of(new(JobPhase.Output, 0, 0)).Core);
        Assert.Equal("Drawing 8 / 8", StatusText.Of(new(JobPhase.Drawing, 8, 8)).Core);
        Assert.Null(StatusText.Of(new(JobPhase.Drawing, 2, 8)).Eta);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(6000, "6,000")]
    [InlineData(99_999, "99,999")]
    [InlineData(123_456, "123.5k")]
    [InlineData(2_400_000, "2400k")]
    public void CountsAreWholeAndCompactWhenHuge(int value, string text) => Assert.Equal(text, StatusText.Count(value));

    /// <summary>Six units per character stands in for the font.</summary>
    private static float Measure(string text) => text.Length * 6;

    [Fact]
    public void RowOneShortensOnlyTheViewNeverTheCounts()
    {
        var line = StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "North elevation, courtyard side", Step = ViewSteps.Sections });
        Assert.Equal("Drawing 3 / 8 · North elevation, courtyard side", line.Row1(Measure, 1000));
        Assert.Equal("Drawing 3 / 8 · North elev…", line.Row1(Measure, Measure("Drawing 3 / 8 · North elev…")));
        // Not even three characters of the view fit: the counts alone, whole.
        Assert.Equal("Drawing 3 / 8", line.Row1(Measure, Measure("Drawing 3 / 8 · No…") - 1));
        Assert.Equal("Drawing 3 / 8", line.Row1(Measure, 10));
        var loading = StatusText.Of(new(JobPhase.Reading, 2400, 6000));
        Assert.Equal("Loading objects 2,400 / 6,000", loading.Row1(Measure, 10));
        // The widest core the layout reserves for is at least as wide as every stage's counts in the 5-digit range.
        foreach (var core in new[] { StatusText.Of(new(JobPhase.Preparing, 99_999, 99_999)).Core, StatusText.Of(new(JobPhase.Reading, 99_999, 99_999)).Core,
            StatusText.Of(new(JobPhase.Queued, 0, 8) { Ahead = 99 }).Core, StatusText.Of(new(JobPhase.Drawing, 998, 999) { Workers = 16 }).Core })
            Assert.True(Measure(core) <= Measure(StatusText.WidestCore), core);
    }

    [Fact]
    public void RowTwoShortensTheStepAfterTheView()
    {
        var line = StatusText.Of(new(JobPhase.Drawing, 2, 8) { View = "Section A", Step = ViewSteps.HiddenLines, StepFraction = .45 }, 41);
        Assert.Equal("hidden lines 45 %", line.Row2(Measure, 200));
        Assert.Equal("hidden li…", line.Row2(Measure, Measure("hidden li…")));
        Assert.Null(line.Row2(Measure, 12));
        Assert.Null(StatusText.Of(new(JobPhase.Reading, 1, 2)).Row2(Measure, 200));
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
