using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class RetentionAdmissionTests
{
    private sealed class Resource : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    [Fact] public void ByteOverflowRetiresOnlyOldestAndHonoursTouches()
    {
        using var lru = new ByteLru<string>(10); var retired = new List<string>();
        lru.Add("a", 4, () => retired.Add("a")); lru.Add("b", 4, () => retired.Add("b"));
        lru.Touch("a"); lru.Add("c", 5, () => retired.Add("c"));
        Assert.Equal(new[] { "b" }, retired); Assert.Equal(9, lru.RetainedBytes); Assert.Equal(2, lru.Count);
        lru.Dispose(); Assert.Equal(new[] { "b", "a", "c" }, retired); Assert.Equal(0, lru.RetainedBytes);
    }
    [Fact] public void LiveRetiredLeaseStaysUsableUntilItsLastRelease()
    {
        using var lru = new ByteLru<string>(8); var value = new Resource();
        var entry = new LeaseEntry<Resource>(value); var a = entry.Acquire(); var b = entry.Acquire();
        lru.Add("old", 8, entry.Dispose); lru.Add("new", 8, () => { });
        Assert.Throws<ObjectDisposedException>(() => entry.Acquire());
        Assert.Same(value, a.Value); Assert.Same(value, b.Value); Assert.Equal(0, value.Disposals);
        a.Dispose(); Assert.Equal(0, value.Disposals); b.Dispose(); Assert.Equal(1, value.Disposals);
    }
    [Fact] public void OversizeEntryIsNotRetainedAndDoesNotFlushUsefulEntries()
    {
        using var lru = new ByteLru<int>(10); var retained = true; var oversizeRetired = false;
        lru.Add(1, 8, () => retained = false); lru.Add(2, 11, () => oversizeRetired = true);
        Assert.True(retained); Assert.True(oversizeRetired); Assert.Equal(8, lru.RetainedBytes);
    }
    [Fact] public void AdmissionAccountsForBytesTopologyAndAccumulatedViews()
    {
        const long budget = 512L * 1024 * 1024;
        Assert.True(new SceneCost(70_000_000, 30_000, 0, 5000).Admitted(6, budget));
        Assert.False(new SceneCost(600_000_000, 0, 0, 1).Admitted(1, budget));
        Assert.False(new SceneCost(1, 100_001, 0, 1).Admitted(6, budget));
        Assert.False(new SceneCost(1, 0, 700_000, 1).Admitted(6, budget));
        var cost = new SceneCost(1000, 6, 0, 1); Assert.True(cost.Estimate(6) > cost.Estimate(1));
        Assert.False(cost.Admitted(0, budget));
    }
}
