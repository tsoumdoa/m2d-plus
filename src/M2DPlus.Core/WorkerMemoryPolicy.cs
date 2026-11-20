namespace M2DPlus.Core;

public readonly record struct MachineMemory(long PhysicalBytes, long AvailableBytes);
public sealed record WorkerSizing(int Count, long CapBytes, long BudgetBytes, long RequiredBytes, string? Refusal)
{
    public long HostReplyBytes { get; init; }
    public long ReplyExpandedLimit => WorkerMemoryPolicy.ReplyExpandedLimit(HostReplyBytes);
}

/// <summary>Hard caps, not expected resident usage. Leave room for Rhino, returned drawings and the OS.</summary>
public static class WorkerMemoryPolicy
{
    public const long GiB = 1024L * 1024 * 1024;
    public const long ReplyStreamAllowance = 1024 * 1024;
    // One streaming receive at a time, including JSON's decoded Drawing byte array.
    // The raw JSON and its base64-decoded bytes need at most 1.75 * expanded length;
    // reserve 2 * length plus stream buffers, without a compressed-frame allocation.
    public static long HostReplyBytes(long budget) => Math.Min(4 * GiB, budget / 4);
    public static long ReplyExpandedLimit(long reservation) => Math.Max(0,
        Math.Min(Array.MaxLength, (reservation - ReplyStreamAllowance) / 2));
    public static int CpuLimit(int logicalCores) => Math.Max(1, logicalCores - 2);
    /// <summary>Each HLD solve is threaded: one worker below 16 cores, two on 16-31, four on 32+. Section work is
    /// largely serial, so four views on 32 threads finished a four-plan set 1.66x faster than two (2026-11-20).</summary>
    /// <param name="overrideValue">Optional M2DPLUS_HLD_WORKERS value; clamped to the CPU ceiling, with invalid values retaining the default.</param>
    public static int ThreadedHldLimit(int logicalCores, string? overrideValue = null) =>
        int.TryParse(overrideValue, out var count) ? Math.Clamp(count, 1, CpuLimit(logicalCores)) :
        logicalCores >= 32 ? 4 : logicalCores >= 16 ? 2 : 1;
    public static long Budget(MachineMemory memory)
    {
        if (memory.PhysicalBytes <= 0 || memory.AvailableBytes <= 0) return 0;
        // Smaller hosts retain the laptop policy. On 32+ GiB leave at least a quarter
        // of physical RAM, and 15% of current headroom, for Rhino, results and the OS.
        // Installed 32 GiB machines usually report slightly less usable RAM.
        var large = memory.PhysicalBytes >= 30 * GiB;
        return Math.Min(memory.PhysicalBytes / 100 * (large ? 75 : 60),
            Math.Min(memory.AvailableBytes, memory.PhysicalBytes) / 100 * (large ? 85 : 80));
    }
    // Preserve planar M/L calibration (3/6 GiB). Curved topology reserves native working
    // memory separately; multiplying it by the preparation/archive factor again would triple count it.
    // See docs/research/worker-memory-sizing.md for the small-model measurements and extrapolation.
    public static long Required(SceneCost scene) => RoundUp(Math.Max(2 * GiB,
        checked(scene.PreparationEstimate(1) * 3 + scene.NativeHldWorkingBytes + GiB)));
    private static long RoundUp(long bytes) => checked((bytes + GiB - 1) / GiB * GiB);
    /// <param name="extra">Bytes a solve needs on top of what the scene alone implies (the dense audit's tiles).</param>
    public static WorkerSizing Size(SceneCost scene, int views, int cpuLimit, MachineMemory memory, long overrideCap = 0, long extra = 0)
    {
        if (views <= 0 || cpuLimit <= 0 || overrideCap < 0 || extra < 0) throw new ArgumentOutOfRangeException(nameof(views));
        var required = RoundUp(checked(Required(scene) + extra));
        var cap = overrideCap > 0 ? overrideCap : required;
        var budget = Budget(memory);
        var reply = HostReplyBytes(budget);
        var count = (int)Math.Min(Math.Min(views, cpuLimit), (budget - reply) / cap);
        return new(count, cap, budget, required, count == 0 ?
            "Worker unavailable: available memory cannot fit one view's cap plus the host reply reservation. Close other applications or reduce the scene/view complexity and run again." : null)
            { HostReplyBytes = reply };
    }
    /// <summary>After every original worker has exited, allow one isolated retry, never an identical-cap retry.
    /// A measured job peak gets 50% growth plus 1 GiB for allocation/serialization bursts. Peaks at a hard cap
    /// are censored observations, not the unknown allocation that failed. Without a peak retain the doubling fallback.</summary>
    public static long RetryCap(long previousCap, MachineMemory memory, long observedPeak = 0)
    {
        var budget = Budget(memory);
        var target = observedPeak > 0
            ? RoundUp(Math.Max(checked(previousCap + GiB), checked(observedPeak + observedPeak / 2 + GiB)))
            : checked(previousCap * 2);
        var cap = Math.Min(target, (budget - HostReplyBytes(budget)) / GiB * GiB);
        // A larger cap below the peak already observed would be a known-futile retry.
        return cap > previousCap && (observedPeak <= 0 || cap > observedPeak) ? cap : 0;
    }
}
