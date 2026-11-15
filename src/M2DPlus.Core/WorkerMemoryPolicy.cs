namespace M2DPlus.Core;

public readonly record struct MachineMemory(long PhysicalBytes, long AvailableBytes);
public sealed record WorkerSizing(int Count, long CapBytes, long BudgetBytes, long RequiredBytes, string? Refusal);

/// <summary>Hard caps, not expected resident usage. Leave room for Rhino, returned drawings and the OS.</summary>
public static class WorkerMemoryPolicy
{
    public const long GiB = 1024L * 1024 * 1024;
    public static int CpuLimit(int logicalCores) => Math.Max(1, logicalCores - 2);
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
    // The measured L view needs ~5 GiB including output archives. Factor 3 plus a 1 GiB
    // process/serialization allowance rounds its 1.62 GiB scene estimate to a 6 GiB cap.
    public static long Required(SceneCost scene) => RoundUp(Math.Max(2 * GiB, checked(scene.Estimate(1) * 3 + GiB)));
    private static long RoundUp(long bytes) => checked((bytes + GiB - 1) / GiB * GiB);
    public static WorkerSizing Size(SceneCost scene, int views, int cpuLimit, MachineMemory memory, long overrideCap = 0)
    {
        if (views <= 0 || cpuLimit <= 0 || overrideCap < 0) throw new ArgumentOutOfRangeException(nameof(views));
        var required = Required(scene);
        var cap = overrideCap > 0 ? overrideCap : required;
        var budget = Budget(memory);
        var count = (int)Math.Min(Math.Min(views, cpuLimit), budget / cap);
        return new(count, cap, budget, required, count == 0 ?
            "Worker unavailable: available memory cannot fit one view's cap. Close other applications or reduce the scene/view complexity and run again." : null);
    }
    /// <summary>After every original worker has exited, allow one isolated retry, never an identical-cap retry.</summary>
    public static long RetryCap(long previousCap, MachineMemory memory)
    {
        var cap = Math.Min(checked(previousCap * 2), Budget(memory) / GiB * GiB);
        return cap > previousCap ? cap : 0;
    }
}
