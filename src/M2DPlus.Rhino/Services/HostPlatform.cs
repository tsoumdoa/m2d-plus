using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>Which drawing route this host has (docs/design/macos.md). Windows: unchanged, with contained workers for
/// sets and heavy views and the fixed 512 MiB in-process budget. Elsewhere (Rhino 8 for Mac): no worker route, because
/// Rhino.Inside runs only on Windows; every drawing runs in Rhino's process under <see cref="InProcessPolicy"/>.</summary>
public static class HostPlatform
{
    private static readonly HostOs Detected = OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Other;
    private static HostOs? _override;
    // Tests on Windows exercise the in-process-only route through the same production code. Never a user setting.
    internal static HostOs? OverrideForTests { get => _override; set { _override = value; } }
    public static HostOs Os => _override ?? Detected;
    public static HostRouteCapabilities Route => HostRouteCapabilities.For(Os);
    public static bool IsolatedWorkers => Route.IsolatedWorkers;

    private static readonly Lazy<NativeScheduler> InProcessOnly = new(() => new(InProcessPolicy.Ceiling(SafeMemory().PhysicalBytes)));
    /// <summary>True while a native drawing holds or waits for the one global slot.</summary>
    public static bool NativeBusy => Scheduler.Busy;
    /// <summary>The one global native scheduler: unchanged on Windows; sized from physical RAM where no workers exist.</summary>
    internal static NativeScheduler Scheduler => IsolatedWorkers ? NativeScheduler.Global : InProcessOnly.Value;

    /// <summary>The interactive-host admission test used by <see cref="NativeDrawing"/>.</summary>
    internal static bool Admitted(SceneCost cost, int views, long budget) =>
        IsolatedWorkers ? cost.Admitted(views, budget) : cost.AdmittedSerial(views, budget);

    /// <summary>Machine memory, or a conservative guess from the GC if the platform probe fails.</summary>
    internal static MachineMemory SafeMemory()
    {
        try { return WorkerMachineMemory.Read(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or EntryPointNotFoundException or DllNotFoundException or InvalidOperationException or OverflowException or PlatformNotSupportedException)
        { return WorkerMachineMemory.Fallback(); }
    }

    public static string Describe() => FormattableString.Invariant(
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}); ") +
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; " +
        $"route={(IsolatedWorkers ? "workers + in-process" : "in-process only")}";
}
