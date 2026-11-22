using M2DPlus.Core;
using M2DPlus.Core.Diagnostics;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class HostRouteTests
{
    private const long G = WorkerMemoryPolicy.GiB, M = 1024L * 1024;

    [Fact]
    public void OnlyWindowsHasTheContainedWorkerGuarantees()
    {
        Assert.Equal(new HostRouteCapabilities(true, true, true), HostRouteCapabilities.For(HostOs.Windows));
        Assert.Equal(new HostRouteCapabilities(false, false, false), HostRouteCapabilities.For(HostOs.MacOS));
        Assert.Equal(new HostRouteCapabilities(false, false, false), HostRouteCapabilities.For(HostOs.Other));
    }

    [Fact]
    public void WindowsInProcessBudgetMatchesTheGlobalScheduler() =>
        Assert.Equal(InProcessPolicy.WindowsBudgetBytes, NativeScheduler.Global.BudgetBytes);

    [Theory]
    [InlineData(8, 3_006_477_107)]     // 35% of 8 GiB
    [InlineData(1, 536_870_912)]       // floor: never below the Windows 512 MiB budget
    [InlineData(64, 24_051_816_857)]   // 35% of 64 GiB
    public void CeilingIsAThirdOfRamWithTheWindowsFloor(long gib, long expected) =>
        Assert.Equal(expected, InProcessPolicy.Ceiling(gib * G));

    [Fact]
    public void BudgetFollowsAvailableMemoryBetweenFloorAndCeiling()
    {
        Assert.Equal(InProcessPolicy.Ceiling(16 * G), InProcessPolicy.Budget(new(16 * G, 16 * G)));
        Assert.Equal((long)(4 * G * 0.6), InProcessPolicy.Budget(new(16 * G, 4 * G)));
        Assert.Equal(InProcessPolicy.MinimumBytes, InProcessPolicy.Budget(new(16 * G, 100 * M)));
        Assert.Equal(InProcessPolicy.MinimumBytes, InProcessPolicy.Budget(new(16 * G, 0)));
    }

    [Fact]
    public void SerialAdmissionDoesNotDivideTopologyAcrossViewsButCountsTheirMemory()
    {
        var faces = new SceneCost(10 * M, 400_000, 0, 100);
        Assert.False(faces.Admitted(2, 64 * G));      // Windows: 600k faces per view share
        Assert.True(faces.AdmittedSerial(2, 64 * G)); // macOS: views draw one after another
        Assert.False(new SceneCost(10 * M, 600_001, 0, 100).AdmittedSerial(1, 64 * G));
        Assert.False(new SceneCost(10 * M, 0, 4_000_001, 100).AdmittedSerial(1, 64 * G));
        Assert.False(new SceneCost(10 * M, 0, 0, 50_000).AdmittedSerial(1, 64 * G));
        Assert.False(faces.AdmittedSerial(0, 64 * G));
        var cost = new SceneCost(100 * M, 1000, 0, 100);
        Assert.True(cost.AdmittedSerial(1, cost.Estimate(1)));
        Assert.False(cost.AdmittedSerial(4, cost.Estimate(1)));
    }

    [Fact]
    public void RefusalExplainsTheMacRouteAndDisclosureStatesLostGuarantees()
    {
        var memory = new MachineMemory(16 * G, 8 * G);
        Assert.Null(InProcessPolicy.Refusal(new SceneCost(M, 10, 10, 2), 4, memory));
        Assert.Null(InProcessPolicy.Refusal(new SceneCost(M, 10, 10, 2), 0, memory));
        var refusal = InProcessPolicy.Refusal(new SceneCost(10 * G, 1000, 0, 100), 1, memory);
        Assert.NotNull(refusal);
        Assert.Contains("no isolated drawing workers on macOS", refusal);
        Assert.Contains("fewer views", refusal);
        var info = InProcessPolicy.Disclosure(new SceneCost(M, 10, 10, 2), 2, memory);
        Assert.StartsWith("Route: in Rhino's process", info);
        Assert.Contains("cancel is cooperative", info);
        Assert.Contains("no hard memory cap", info);
        Assert.Contains("RAM 16.0 GiB, available 8.0 GiB", info);
    }

    [Fact]
    public void MacAvailableMemoryIsFreePlusInactivePagesClampedToRam()
    {
        // 16 KiB pages (Apple silicon): 100k free + 200k inactive = 4.58 GiB of a 16 GiB machine.
        Assert.Equal(new MachineMemory(16 * G, 300_000L * 16384), MacMemory.From(16 * G, 16384, 100_000, 200_000));
        Assert.Equal(new MachineMemory(8 * G, 8 * G), MacMemory.From(8 * G, 4096, 3_000_000, 3_000_000));
        Assert.Equal(new MachineMemory(8 * G, 0), MacMemory.From(8 * G, 4096, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacMemory.From(0, 4096, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacMemory.From(G, 0, 1, 1));
    }
}

public sealed class DrawingSignatureTests
{
    private static DrawingSignature View(string name = "plan", double length = 1000, int pieces = 12, int regions = 2, double area = 5000, string? error = null) =>
        new(name, error, 12000, 9500, new Dictionary<string, ClassSignature>
        {
            ["Visible"] = new(pieces, 0, length, -10, -20, 9000, 7000),
            ["Hidden"] = new(4, 1, 250, 0, 0, 4000, 3000),
        }, regions, regions, area);

    [Fact]
    public void IdenticalViewsMatchAndSegmentationIsOnlyANote()
    {
        Assert.True(SignatureComparison.Compare(View(), View()).Passed);
        var split = SignatureComparison.Compare(View(), View(pieces: 15, length: 1003));
        Assert.True(split.Passed);
        Assert.Contains(split.Notes, n => n.StartsWith("Visible pieces: expected 12, got 15", StringComparison.Ordinal));
    }

    [Fact]
    public void LengthAreaRegionAndErrorDifferencesFail()
    {
        Assert.Contains(SignatureComparison.Compare(View(), View(length: 1020)).Failures, f => f.StartsWith("Visible length", StringComparison.Ordinal));
        Assert.Contains(SignatureComparison.Compare(View(), View(regions: 3)).Failures, f => f.StartsWith("cut regions", StringComparison.Ordinal));
        Assert.Contains(SignatureComparison.Compare(View(), View(area: 5100)).Failures, f => f.StartsWith("cut area", StringComparison.Ordinal));
        Assert.Contains(SignatureComparison.Compare(View(), View(error: "boom")).Failures, f => f.StartsWith("error", StringComparison.Ordinal));
        var moved = View() with { Classes = new Dictionary<string, ClassSignature>(View().Classes) { ["Visible"] = new(12, 0, 1000, 500, -20, 9000, 7000) } };
        Assert.Contains(SignatureComparison.Compare(View(), moved).Failures, f => f.StartsWith("Visible extents", StringComparison.Ordinal));
        var missing = View() with { Classes = new Dictionary<string, ClassSignature> { ["Visible"] = View().Classes["Visible"] } };
        Assert.Contains(SignatureComparison.Compare(View(), missing).Failures, f => f.StartsWith("Hidden length", StringComparison.Ordinal));
    }

    [Fact]
    public void BaselineRoundTripsAndFlagsMissingOrExtraViews()
    {
        var baseline = new SignatureBaseline(SignatureBaseline.CurrentSchema, "fixture", "test", [View("plan"), View("section")]);
        var back = SignatureBaseline.FromJson(baseline.ToJson());
        Assert.Equal(baseline.ToJson(), back.ToJson());
        Assert.All(SignatureComparison.Compare(back, [View("plan"), View("section")]), r => Assert.True(r.Passed));
        var results = SignatureComparison.Compare(back, [View("plan"), View("elevation")]);
        Assert.Contains(results, r => r.View == "section" && r.Failures.Single() == "view missing from this run");
        Assert.Contains(results, r => r.View == "elevation" && r.Failures.Single() == "view not in the baseline");
    }
}
