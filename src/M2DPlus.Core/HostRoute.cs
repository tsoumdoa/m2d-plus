namespace M2DPlus.Core;

/// <summary>The operating system M2D+ is running on, as far as the drawing route is concerned.</summary>
public enum HostOs { Windows, MacOS, Other }

/// <summary>What the drawing route can guarantee on a host OS (docs/design/macos.md). Windows has the contained worker
/// route: process isolation, a hard Job Object memory cap and hard kill. Rhino.Inside, which the worker needs, runs only
/// on Windows, so every other host draws in Rhino's own process with cooperative cancellation and no hard cap.</summary>
public readonly record struct HostRouteCapabilities(bool IsolatedWorkers, bool HardMemoryCap, bool HardCancel)
{
    public static HostRouteCapabilities For(HostOs os) => os == HostOs.Windows ? new(true, true, true) : new(false, false, false);
}

/// <summary>In-process admission when there is no worker route (macOS). The interactive host is then the only route,
/// so sets of views and heavier single views draw in Rhino's process, one view after another, within a memory
/// budget read from the machine at each run. Windows keeps its fixed 512 MiB in-process budget and worker route.</summary>
public static class InProcessPolicy
{
    /// <summary>The Windows in-process budget (<see cref="NativeScheduler.Global"/>); also the macOS ceiling floor.</summary>
    public const long WindowsBudgetBytes = 512L * 1024 * 1024;
    /// <summary>Below this, a drawing is small enough to always try, however little memory macOS reports free.</summary>
    public const long MinimumBytes = 256L * 1024 * 1024;
    /// <summary>No hard cap exists in-process: stay well below physical RAM, leaving Rhino, other apps and the
    /// returned drawing room, and below most of what is available now.</summary>
    public const double PhysicalFraction = 0.35, AvailableFraction = 0.6;

    /// <summary>The fixed scheduler budget on a host without workers: 35% of physical RAM, at least 512 MiB.</summary>
    public static long Ceiling(long physicalBytes) => Math.Max(WindowsBudgetBytes, (long)(Math.Max(0, physicalBytes) * PhysicalFraction));

    /// <summary>The budget for one run now: the ceiling, further limited to 60% of available memory, but never below
    /// <see cref="MinimumBytes"/>.</summary>
    public static long Budget(MachineMemory memory) =>
        Math.Min(Ceiling(memory.PhysicalBytes), Math.Max(MinimumBytes, (long)(Math.Max(0, memory.AvailableBytes) * AvailableFraction)));

    /// <summary>Null when the job may draw in-process now; otherwise the user-facing refusal.</summary>
    public static string? Refusal(SceneCost cost, int views, MachineMemory memory)
    {
        if (views <= 0) return null;
        var budget = Budget(memory);
        if (cost.AdmittedSerial(views, budget)) return null;
        return FormattableString.Invariant(
            $"Too large to draw inside Rhino on this platform: estimate {cost.Estimate(views) / (double)WorkerMemoryPolicy.GiB:F2} GiB for {views} view(s), ") +
            FormattableString.Invariant($"{cost.BrepFaces} Brep faces, {cost.MeshTriangles} mesh triangles, {cost.Sources} sources; in-process budget {budget / (double)WorkerMemoryPolicy.GiB:F2} GiB ") +
            "(35% of RAM, at most 60% of memory available now). There are no isolated drawing workers on macOS. Close other applications, " +
            "draw fewer views at once, or narrow Geometry or the view volumes, then run again.";
    }

    /// <summary>The Info line for a view drawn on the in-process-only route.</summary>
    public static string Disclosure(SceneCost cost, int views, MachineMemory memory) => FormattableString.Invariant(
        $"Route: in Rhino's process (macOS has no isolated drawing workers); views draw one after another; cancel is cooperative and a native call may finish in the background; no hard memory cap. ") +
        FormattableString.Invariant($"Admission: estimate {cost.Estimate(views) / (double)WorkerMemoryPolicy.GiB:F2} GiB within budget {Budget(memory) / (double)WorkerMemoryPolicy.GiB:F2} GiB ") +
        FormattableString.Invariant($"(RAM {memory.PhysicalBytes / (double)WorkerMemoryPolicy.GiB:F1} GiB, available {memory.AvailableBytes / (double)WorkerMemoryPolicy.GiB:F1} GiB).");
}

/// <summary>macOS memory arithmetic, kept free of the Mach calls so it can be tested anywhere.</summary>
public static class MacMemory
{
    /// <summary>Available bytes from <c>host_statistics64(HOST_VM_INFO64)</c> page counts: free plus inactive pages
    /// (the reclaimable file cache), as psutil and Activity Monitor's "available" estimate do. <c>free_count</c> already
    /// includes speculative pages, so they are not added again; purgeable pages are left out to stay conservative.
    /// Clamped to physical memory.</summary>
    public static MachineMemory From(long physicalBytes, long pageSize, ulong freePages, ulong inactivePages)
    {
        if (physicalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(physicalBytes));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var pages = freePages + inactivePages;
        var available = pages > (ulong)(physicalBytes / pageSize) ? physicalBytes : checked((long)pages * pageSize);
        return new(physicalBytes, Math.Min(physicalBytes, available));
    }
}
