using System.Runtime.InteropServices;
using M2DPlus.Core;
using M2DPlus.Core.Diagnostics;
using M2DPlus.Rhino.Services;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>Layout of the Mach statistics the macOS memory probe reads; no Rhino needed.</summary>
public sealed class MacProbeLayoutTests
{
    [Fact]
    public void VmStatistics64MatchesTheMachHeader()
    {
        // mach/vm_statistics.h: sizeof(vm_statistics64_data_t) == 152, HOST_VM_INFO64_COUNT == 38.
        Assert.Equal(152, Marshal.SizeOf<MacMachineMemory.VmStatistics64>());
        Assert.Equal(38, MacMachineMemory.VmInfoCount);
        Assert.Equal(88, (int)Marshal.OffsetOf<MacMachineMemory.VmStatistics64>(nameof(MacMachineMemory.VmStatistics64.PurgeableCount)));
        Assert.Equal(128, (int)Marshal.OffsetOf<MacMachineMemory.VmStatistics64>(nameof(MacMachineMemory.VmStatistics64.CompressorPageCount)));
    }

}

/// <summary>The platform self-test fixture on Windows: it must match the committed baseline that the Mac self-test
/// compares with, and the in-process-only (macOS) route must draw the same signatures through the same code.
/// Set M2DPLUS_WRITE_SELFTEST_BASELINE to a file path to regenerate the baseline (Windows only).</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class PlatformSelfTestTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoFact]
    public void WindowsKeepsTheContainedRouteAndItsScheduler() => host.Run(() =>
    {
        Assert.Equal(HostOs.Windows, HostPlatform.Os);
        Assert.True(HostPlatform.IsolatedWorkers);
        Assert.Same(NativeScheduler.Global, HostPlatform.Scheduler);
        Assert.Same(NativeScheduler.Global, new NativeSettings().Scheduler);
        Assert.Null(new NativeSettings().AdmitInSlot);
    });

    [RhinoFact]
    public void InProcessOnlyAdmissionIsCheckedAgainWithLiveMemoryInsideTheSlot() => host.Run(() =>
    {
        using var document = global::Rhino.RhinoDoc.CreateHeadless(null);
        document.ModelUnitSystem = global::Rhino.UnitSystem.Millimeters; document.ModelAbsoluteTolerance = 0.01;
        using var cache = new SceneCache();
        using var lease = BaselineTests.Capture(document, cache, PlatformSelfTest.FixtureInputs());
        var views = PlatformSelfTest.FixtureViews().Take(2).ToArray();
        var identity = new RequestIdentity(lease.Value.Revision, "admit", "admit", 1);
        // A refusal in the slot stops the job before preparation or HLD.
        var calls = NativeDrawing.HldCalls;
        var refused = Assert.Throws<InvalidOperationException>(() => NativeDrawing.SolveBorrowedAsync(lease.Value, identity, views,
            new NativeSettings { AdmitInSlot = () => "refused in slot" }, default, null).GetAwaiter().GetResult());
        Assert.Equal("refused in slot", refused.Message);
        Assert.Equal(calls, NativeDrawing.HldCalls);
        // The second (in-slot) reading is the one disclosed.
        const long G = WorkerMemoryPolicy.GiB;
        var readings = new Queue<MachineMemory>([new(16 * G, 8 * G), new(16 * G, 4 * G)]);
        using var batch = DrawingExecution.SolveInProcessOnlyAsync(lease.Value, identity, views, new(), default, null,
            () => readings.Count > 1 ? readings.Dequeue() : readings.Peek()).GetAwaiter().GetResult();
        Assert.All(batch.Views, v => Assert.Contains(v.Diagnostics, d => d.Contains("RAM 16.0 GiB, available 4.0 GiB", StringComparison.Ordinal)));
    });

    [RhinoFact]
    public void SelfTestPassesAgainstTheCommittedWindowsBaseline() => host.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("M2DPLUS_WRITE_SELFTEST_BASELINE") is { Length: > 0 } path)
        {
            using var batch = PlatformSelfTest.DrawFixture(default);
            var baseline = PlatformSelfTest.Baseline(batch.Views.Select(v => PlatformSelfTest.Signature(v)).ToArray());
            File.WriteAllText(path, baseline.ToJson() + Environment.NewLine);
            output.WriteLine("Wrote " + path);
        }
        var report = PlatformSelfTest.Run(PlatformSelfTest.EmbeddedBaseline() is null && Environment.GetEnvironmentVariable("M2DPLUS_WRITE_SELFTEST_BASELINE") is { Length: > 0 } written
            ? SignatureBaseline.FromJson(File.ReadAllText(written)) : null);
        output.WriteLine(report.Text);
        Assert.True(report.Passed, report.Text);
        Assert.Equal(["plan", "elevation", "axonometric", "section"], report.Signatures.Select(s => s.View));
        Assert.All(report.Signatures, s => Assert.Null(s.Error));
        var section = report.Signatures.Single(s => s.View == "section");
        Assert.True(section.Regions >= 3, "The section cuts the box, cylinder and sphere.");
        Assert.True(section.RegionArea > 0);
        Assert.All(report.Signatures.Where(s => s.View != "section"), s => Assert.True(s.Classes["Visible"].Length > 0));
    });

    [RhinoFact]
    public void InProcessOnlyRouteDrawsTheSameFixtureAndDisclosesItsGuarantees() => host.Run(() =>
    {
        DrawingSignature[] windows;
        using (var batch = PlatformSelfTest.DrawFixture(default))
        {
            windows = batch.Views.Select(v => PlatformSelfTest.Signature(v)).ToArray();
            Assert.All(batch.Views, v => Assert.DoesNotContain(v.Diagnostics, d => d.StartsWith("Route: in Rhino's process", StringComparison.Ordinal)));
        }
        HostPlatform.OverrideForTests = HostOs.MacOS;
        try
        {
            Assert.False(HostPlatform.IsolatedWorkers);
            Assert.False(DrawingWorkerPool.Shared.Installed);
            Assert.NotSame(NativeScheduler.Global, HostPlatform.Scheduler);
            Assert.True(HostPlatform.Scheduler.BudgetBytes >= InProcessPolicy.WindowsBudgetBytes);
            using var batch = PlatformSelfTest.DrawFixture(default);
            Assert.All(batch.Views, v => Assert.Contains(v.Diagnostics, d => d.StartsWith("Route: in Rhino's process", StringComparison.Ordinal)));
            var mac = batch.Views.Select(v => PlatformSelfTest.Signature(v)).ToArray();
            var results = SignatureComparison.Compare(new SignatureBaseline(1, PlatformSelfTest.Fixture, "windows route", windows), mac);
            foreach (var result in results) output.WriteLine($"{result.View}: {(result.Passed ? "match" : string.Join(", ", result.Failures))} {string.Join(", ", result.Notes)}");
            Assert.All(results, r => Assert.True(r.Passed, r.View + ": " + string.Join(", ", r.Failures)));
            var report = PlatformSelfTest.Run();
            output.WriteLine(report.Text);
            // Only the build check fails here: this is the Windows build pretending to be on a Mac.
            Assert.Equal(["platform"], report.Checks.Where(c => !c.Passed).Select(c => c.Name));
        }
        finally { HostPlatform.OverrideForTests = null; }
    });
}
