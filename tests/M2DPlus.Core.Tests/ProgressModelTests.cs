using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>The job stage and progress model (owner feedback on beta.1: "Queued" was vague; UX §5.2).</summary>
public sealed class ProgressModelTests
{
    private static IEnumerable<JobProgress> Job(int objects, int views, bool workers = false)
    {
        for (var i = 0; i <= objects; i += objects / 4) yield return new(JobPhase.Reading, i, objects);
        yield return new(JobPhase.Queued, 0, views) { Ahead = 1 };
        for (var i = 0; i <= objects; i += objects / 4) yield return new(JobPhase.Preparing, i, objects);
        for (var v = 0; v < views; v++)
        {
            if (workers) { yield return new(JobPhase.Drawing, v, views) { Workers = 3 }; continue; }
            yield return new(JobPhase.Drawing, v, views) { View = $"V{v}" };
            yield return new(JobPhase.Drawing, v, views) { View = $"V{v}", Step = ViewSteps.ExactLines };
            foreach (var f in new[] { 0, .3, .7, 1 }) yield return new(JobPhase.Drawing, v, views) { View = $"V{v}", Step = ViewSteps.HiddenLines, StepFraction = f };
            yield return new(JobPhase.Drawing, v, views) { View = $"V{v}", Step = ViewSteps.Sections };
            yield return new(JobPhase.Drawing, v, views) { View = $"V{v}", Step = ViewSteps.Output };
        }
        yield return new(JobPhase.Drawing, views, views);
        yield return new(JobPhase.Complete, views, views);
        yield return new(JobPhase.Output, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBarOnlyMovesForward(bool workers)
    {
        var model = new JobProgressModel(null);
        var last = 0d; var time = 0d;
        foreach (var report in Job(6000, 8, workers))
        {
            var state = model.Update(report, time += .1);
            Assert.InRange(state.Fraction, last, 1);
            Assert.InRange(state.SpanEnd, state.Fraction, 1);
            last = state.Fraction;
        }
        // A late or out-of-order report never moves it back.
        Assert.Equal(last, model.Update(new(JobPhase.Reading, 10, 6000), time).Fraction);
        Assert.Equal(last, model.Update(new(JobPhase.Drawing, 1, 8), time).Fraction);
    }

    [Fact]
    public void DefaultWeightsGiveEachStageItsShare()
    {
        var model = new JobProgressModel(null);
        Assert.Equal(.15, model.Update(new(JobPhase.Reading, 10, 10), 1).Fraction, 6);
        Assert.Equal(.25, model.Update(new(JobPhase.Preparing, 10, 10), 1).Fraction, 6);
        Assert.Equal(.25 + .70 * 4 / 8, model.Update(new(JobPhase.Drawing, 4, 8), 1).Fraction, 6);
        Assert.Equal(.95, model.Update(new(JobPhase.Drawing, 8, 8), 1).Fraction, 6);
        Assert.Equal(1, model.Update(new(JobPhase.Complete, 8, 8), 1).Fraction, 6);
    }

    [Fact]
    public void HistoryWeightsAreTheTimeShareOfEachStageAndReusedObjectsSkipLoading()
    {
        // Last time: 1 s loading, 1 s preparing, 7 s drawing, 1 s output.
        var history = new StageTimes(1, 1, 7, 1);
        var model = new JobProgressModel(history);
        Assert.True(model.HasHistory);
        Assert.Equal(.1, model.Update(new(JobPhase.Reading, 5, 5), 1).Fraction, 6);
        Assert.Equal(.2, model.Update(new(JobPhase.Preparing, 5, 5), 2).Fraction, 6);
        var reused = new JobProgressModel(history, loading: false);
        // Without loading, preparing ends at 1/9 of the whole.
        Assert.Equal(1d / 9, reused.Update(new(JobPhase.Preparing, 5, 5), 1).Fraction, 6);
        // A stage that took no time last time still gets a sliver, so it is visible.
        var quick = new JobProgressModel(new StageTimes(0, 0, 10, 0));
        Assert.True(quick.Update(new(JobPhase.Reading, 1, 1), 1).Fraction > 0);
    }

    [Fact]
    public void StagesWithoutSubProgressAreIndeterminateNotFrozen()
    {
        var model = new JobProgressModel(null);
        var waiting = model.Update(new(JobPhase.Queued, 0, 4) { Ahead = 2 }, 1);
        Assert.True(waiting.Indeterminate);
        Assert.True(model.Update(new(JobPhase.Preparing, 0, 0), 1).Indeterminate);
        Assert.False(model.Update(new(JobPhase.Preparing, 10, 100), 1).Indeterminate);
        // Workers report whole views: the shimmer covers the views in flight.
        var workers = model.Update(new(JobPhase.Drawing, 2, 8) { Workers = 3 }, 2);
        Assert.True(workers.Indeterminate);
        Assert.Equal(.25 + .70 * 5 / 8, workers.SpanEnd, 6);
        // In process, hidden lines report their own fraction: determinate.
        var hidden = model.Update(new(JobPhase.Drawing, 3, 8) { Step = ViewSteps.HiddenLines, StepFraction = .5 }, 3);
        Assert.False(hidden.Indeterminate);
        Assert.True(model.Update(new(JobPhase.Output, 0, 0), 4).Indeterminate);
    }

    [Fact]
    public void InnerStepFractionsMoveTheBarWithinAView()
    {
        // Exact lines (the planar kernel) and checking (ray verification) report their own fraction, like hidden lines.
        var model = new JobProgressModel(null, loading: false);
        var exact = model.Update(new(JobPhase.Drawing, 0, 1) { Step = ViewSteps.ExactLines, StepFraction = .5 }, 1);
        Assert.False(exact.Indeterminate);
        var checking = model.Update(new(JobPhase.Drawing, 0, 1) { Step = ViewSteps.Checking, StepFraction = .5 }, 2);
        Assert.True(checking.Fraction > exact.Fraction);
        // One worker forwards its one view's steps: determinate too. Parallel workers keep whole views.
        var single = new JobProgressModel(null, loading: false);
        var one = single.Update(new(JobPhase.Drawing, 0, 1) { Workers = 1, Step = ViewSteps.HiddenLines, StepFraction = .5 }, 1);
        Assert.False(one.Indeterminate);
        Assert.True(one.Fraction > single.Update(new(JobPhase.Preparing, 1, 1), 1).Fraction - 1e-9);
        var parallel = new JobProgressModel(null, loading: false);
        var many = parallel.Update(new(JobPhase.Drawing, 0, 4) { Workers = 3, Step = ViewSteps.HiddenLines, StepFraction = .5 }, 1);
        Assert.True(many.Indeterminate);
        Assert.True(one.Fraction > many.Fraction);
    }

    [Fact]
    public void TheEstimateNeedsEnoughHistory()
    {
        // No history: nothing until a quarter of the way and four seconds in.
        var fresh = new JobProgressModel(null);
        Assert.Null(fresh.Update(new(JobPhase.Preparing, 5, 10), 1).RemainingSeconds);
        Assert.Null(fresh.Update(new(JobPhase.Drawing, 1, 8), 3).RemainingSeconds);
        var state = fresh.Update(new(JobPhase.Drawing, 2, 8), 10);
        Assert.NotNull(state.RemainingSeconds);
        Assert.Equal(10 * (1 - state.Fraction) / state.Fraction, state.RemainingSeconds!.Value, 6);
        // With history the weights are time shares, so an early estimate is meaningful.
        var known = new JobProgressModel(new StageTimes(2, 1, 30, 1));
        var early = known.Update(new(JobPhase.Preparing, 10, 10), 3);
        Assert.NotNull(early.RemainingSeconds);
        Assert.InRange(early.RemainingSeconds!.Value, 25, 40);
        // Under two seconds left: no estimate.
        Assert.Null(known.Update(new(JobPhase.Drawing, 39, 40), 34).RemainingSeconds);
    }

    [Theory]
    [InlineData(4.4, "~4 s left")]
    [InlineData(17, "~17 s left")]
    [InlineData(41, "~40 s left")]
    [InlineData(88, "~90 s left")]
    [InlineData(170, "~3 min left")]
    public void EstimatesAreRound(double seconds, string text) => Assert.Equal(text, JobProgressModel.Eta(seconds));

    [Fact]
    public void EnginePhasesMapToUserSteps()
    {
        Assert.Equal(ViewSteps.HiddenLines, ViewSteps.Of("native solve"));
        Assert.Equal(ViewSteps.ExactLines, ViewSteps.Of("kernel solve"));
        Assert.Equal(ViewSteps.Sections, ViewSteps.Of("sections"));
        Assert.Equal(ViewSteps.Checking, ViewSteps.Of("native ray verification"));
        Assert.Equal(ViewSteps.ExactCurved, ViewSteps.Of("exact curved"));
        Assert.Equal(ViewSteps.Output, ViewSteps.Of("output construction"));
        Assert.Null(ViewSteps.Of("normalisation"));
    }

    [Fact]
    public async Task TheSchedulerSaysHowManyAreAhead()
    {
        var scheduler = new NativeScheduler(1024);
        using var release = new ManualResetEventSlim();
        var first = scheduler.RunAsync(1, _ => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, default);
        var ahead = new List<int>();
        var second = scheduler.RunAsync(1, _ => 2, default, n => { lock (ahead) ahead.Add(n); });
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        release.Set();
        Assert.Equal(2, await second);
        Assert.Equal([1], ahead);
        Assert.Equal(1, await first);
        // Nobody holds the slot: no waiting report at all.
        var none = new List<int>();
        Assert.Equal(3, await scheduler.RunAsync(1, _ => 3, default, none.Add));
        Assert.Empty(none);
    }
}
