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
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception();
        // Also honor available commit; RAM alone cannot promise that an allocation can be committed.
        return new(checked((long)status.Physical), checked((long)Math.Min(status.AvailablePhysical, status.AvailablePageFile)));
    }
}
