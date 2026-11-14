using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ExecutionTests
{
    private sealed class Resource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    [Fact] public void EvictionWaitsForAllLeasesAndReleaseIsIdempotent()
    {
        var value = new Resource();
        var entry = new LeaseEntry<Resource>(value);
        var a = entry.Acquire(); var b = entry.Acquire();
        entry.Dispose(); entry.Dispose();
        Assert.Throws<ObjectDisposedException>(() => entry.Acquire());
        a.Dispose(); a.Dispose();
        Assert.Same(value, b.Value); Assert.Equal(0, value.Disposals);
        b.Dispose(); Assert.Equal(1, value.Disposals);
        Assert.Throws<ObjectDisposedException>(() => b.Value);
    }
    [Fact] public void UnleasedEntryDisposesImmediately()
    {
        var value = new Resource(); var entry = new LeaseEntry<Resource>(value);
        entry.Dispose(); entry.Dispose(); Assert.Equal(1, value.Disposals);
    }
    [Fact] public async Task ConcurrentRetirementAndReleaseDisposeExactlyOnce()
    {
        for (var i = 0; i < 100; i++)
        {
            var value = new Resource(); var entry = new LeaseEntry<Resource>(value);
            var leases = Enumerable.Range(0, 10).Select(_ => entry.Acquire()).ToArray();
            await Task.WhenAll(leases.Select(l => Task.Run(l.Dispose)).Append(Task.Run(entry.Dispose)));
            Assert.Equal(1, value.Disposals);
        }
    }
    [Fact] public void OnlyCompleteCurrentUncancelledIdentityPublishes()
    {
        using var gate = new RequestGate();
        var a = gate.Begin("s", "v", "o"); var b = gate.Begin("s", "v", "o");
        Assert.True(a.Token.IsCancellationRequested); Assert.True(b.Identity.Generation > a.Identity.Generation);
        var published = 0;
        Assert.False(gate.TryPublish(a.Identity, true, () => published++));
        Assert.False(gate.TryPublish(b.Identity, false, () => published++));
        Assert.False(gate.TryPublish(b.Identity with { ViewRevision = "other" }, true, () => published++));
        Assert.True(gate.TryPublish(b.Identity, true, () => published++));
        gate.Cancel(); Assert.False(gate.TryPublish(b.Identity, true, () => published++));
        Assert.False(gate.TryUpdate(b.Identity, () => published++));
        Assert.Equal(1, published);
    }
    [Fact] public void DisposalRejectsCompletionAndNewWork()
    {
        var gate = new RequestGate(); var request = gate.Begin("s", "v", "o"); gate.Dispose(); gate.Dispose();
        Assert.False(gate.IsCurrent(request.Identity)); Assert.True(request.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => gate.Begin("s", "v", "o"));
    }
    [Fact] public async Task CancellationDoesNotFreeNativeSlotBeforeActualReturn()
    {
        var scheduler = new NativeScheduler(100);
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = scheduler.RunAsync(50, _ => { entered.SetResult(); release.Wait(); return 1; }, cancel.Token);
        await entered.Task; cancel.Cancel();
        var secondEntered = false;
        var second = scheduler.RunAsync(50, _ => { secondEntered = true; return 2; }, default);
        Assert.False(secondEntered); Assert.False(second.IsCompleted);
        release.Set(); Assert.Equal(1, await first); Assert.Equal(2, await second);
    }
    [Fact] public async Task QueuedCancellationAndNativeFailureDoNotLeakSlot()
    {
        var scheduler = new NativeScheduler(100);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.RunAsync<int>(1, _ => { entered.SetResult(); release.Wait(); throw new IOException(); }, default);
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var waiting = scheduler.RunAsync<int>(1, _ => throw new Exception("Must not run"), cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.Set(); await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Equal(42, await scheduler.RunAsync(1, _ => 42, default));
    }
    [Theory] [InlineData(-1)] [InlineData(101)]
    public async Task BudgetRejectsBeforeCallingNative(long estimate)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeScheduler(100).RunAsync(estimate, _ => 1, default));
    }
    [Fact] public void InvalidBudgetRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new NativeScheduler(0));
    [Fact] public void QualityDoesNotDependOnExecutionState()
    {
        var report = new QualityReport("Native", 2, 1, new Dictionary<string, int> { ["Text"] = 1 }, new[] { "SubD to Brep" }, true);
        Assert.True(report.Quarantined); Assert.Equal(2, report.UncertainCount);
        Assert.Equal(1, report.UnsupportedByType["Text"]);
        Assert.Equal(new JobProgress(JobPhase.Complete, 2, 2), new(JobPhase.Complete, 2, 2));
    }
}
