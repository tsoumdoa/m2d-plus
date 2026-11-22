using System.ComponentModel;
using System.Runtime.InteropServices;
using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

internal static class WorkerMachineMemory
{
    [StructLayout(LayoutKind.Sequential)] private struct Status
    {
        public uint Length, Load;
        public ulong Physical, AvailablePhysical, PageFile, AvailablePageFile, Virtual, AvailableVirtual, ExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref Status status);
    internal static MachineMemory Read()
    {
        if (OperatingSystem.IsMacOS()) return MacMachineMemory.Read();
        if (!OperatingSystem.IsWindows()) return Fallback();
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception();
        // Also honor available commit; RAM alone cannot promise that an allocation can be committed.
        return new(checked((long)status.Physical), checked((long)Math.Min(status.AvailablePhysical, status.AvailablePageFile)));
    }
    /// <summary>The GC's view of the machine when no platform probe is available: total memory, and that less the
    /// current memory load, so admission never treats an unknown machine as empty.</summary>
    internal static MachineMemory Fallback()
    {
        var info = GC.GetGCMemoryInfo();
        long physical = 0;
        if (OperatingSystem.IsMacOS())
            try { physical = MacMachineMemory.Physical(); }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { }
        var total = Math.Max(InProcessPolicy.WindowsBudgetBytes, physical > 0 ? physical : info.TotalAvailableMemoryBytes);
        return new(total, Math.Clamp(total - info.MemoryLoadBytes, 0, total));
    }
}

/// <summary>macOS physical and available memory: <c>sysctl hw.memsize</c> and Mach <c>host_statistics64</c>. The
/// arithmetic is <see cref="MacMemory.From"/>; this class only reads the kernel.</summary>
internal static class MacMachineMemory
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostVmInfo64 = 4;
    // vm_statistics64 (mach/vm_statistics.h), naturally aligned: 152 bytes, HOST_VM_INFO64_COUNT = 38 integers.
    [StructLayout(LayoutKind.Sequential)] internal struct VmStatistics64
    {
        public uint FreeCount, ActiveCount, InactiveCount, WireCount;
        public ulong ZeroFillCount, Reactivations, Pageins, Pageouts, Faults, CowFaults, Lookups, Hits, Purges;
        public uint PurgeableCount, SpeculativeCount;
        public ulong Decompressions, Compressions, Swapins, Swapouts;
        public uint CompressorPageCount, ThrottledCount, ExternalPageCount, InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }
    internal static readonly int VmInfoCount = Marshal.SizeOf<VmStatistics64>() / sizeof(int);
    [DllImport(LibSystem, EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out long value, ref nint size, IntPtr newValue, nint newSize);
    [DllImport(LibSystem, EntryPoint = "mach_host_self")] private static extern uint MachHostSelf();
    [DllImport(LibSystem, EntryPoint = "host_page_size")] private static extern int HostPageSize(uint host, out nuint size);
    [DllImport(LibSystem, EntryPoint = "host_statistics64")] private static extern int HostStatistics64(uint host, int flavor, [Out] uint[] info, ref uint count);
    private const int MigArrayTooLarge = -307;
    // One send right for the process lifetime; mach_host_self adds a reference per call.
    private static readonly Lazy<uint> Host = new(MachHostSelf);

    /// <summary>Physical RAM alone (<c>sysctl hw.memsize</c>), so a VM-statistics failure never hides the machine size.</summary>
    internal static long Physical()
    {
        nint size = sizeof(long);
        if (SysctlByName("hw.memsize", out var physical, ref size, IntPtr.Zero, 0) != 0 || size != sizeof(long) || physical <= 0)
            throw new InvalidOperationException("sysctl hw.memsize failed (errno " + Marshal.GetLastPInvokeError() + ").");
        return physical;
    }

    internal static MachineMemory Read()
    {
        var physical = Physical();
        if (HostPageSize(Host.Value, out var page) != 0 || page == 0) throw new InvalidOperationException("host_page_size failed.");
        // Newer kernels may want more than the 38-word revision-one structure (MIG_ARRAY_TOO_LARGE, with the required
        // count returned): ask with a generous buffer, retry once at the returned size, then read the stable prefix.
        var buffer = new uint[256];
        var count = (uint)buffer.Length;
        var result = HostStatistics64(Host.Value, HostVmInfo64, buffer, ref count);
        if (result == MigArrayTooLarge && count > buffer.Length)
        {
            buffer = new uint[count];
            result = HostStatistics64(Host.Value, HostVmInfo64, buffer, ref count);
        }
        if (result != 0) throw new InvalidOperationException("host_statistics64 failed (kern_return " + result + ", count " + count + ").");
        if (count < VmInfoCount) throw new InvalidOperationException("host_statistics64 returned " + count + " words; expected at least " + VmInfoCount + ".");
        var stats = MemoryMarshal.Read<VmStatistics64>(MemoryMarshal.AsBytes(buffer.AsSpan()));
        return MacMemory.From(physical, checked((long)page), stats.FreeCount, stats.InactiveCount);
    }
}
