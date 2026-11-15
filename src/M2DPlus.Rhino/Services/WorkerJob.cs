using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace M2DPlus.Rhino.Services;

/// <summary>Only handles to processes started by WorkerClient are assigned here.</summary>
internal sealed class WorkerJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Basic { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public Basic Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, out Limits limits, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct Port { public IntPtr Key, Handle; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateIoCompletionPort(IntPtr file,IntPtr existing,UIntPtr key,uint threads);
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="SetInformationJobObject")] private static extern bool SetPort(IntPtr job,int kind,ref Port port,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetQueuedCompletionStatus(IntPtr port,out uint code,out UIntPtr key,out IntPtr process,uint timeout);
    private IntPtr _handle, _port;
    private bool _memoryLimitHit;
    internal bool MemoryLimitHit
    {
        get
        {
            while(_port!=IntPtr.Zero && GetQueuedCompletionStatus(_port,out var code,out _,out _,0))
                if(code is 9 or 10)_memoryLimitHit=true;
            return _memoryLimitHit;
        }
    }
    internal WorkerJob(long cap)
    {
        if(cap<=0)throw new ArgumentOutOfRangeException(nameof(cap));
        _handle=CreateJobObject(IntPtr.Zero,null);
        if(_handle==IntPtr.Zero)throw new Win32Exception();
        _port=CreateIoCompletionPort(new IntPtr(-1),IntPtr.Zero,UIntPtr.Zero,1);
        var port=new Port{Key=new IntPtr(1),Handle=_port};
        if(_port==IntPtr.Zero || !SetPort(_handle,7,ref port,(uint)Marshal.SizeOf<Port>())){var error=new Win32Exception();Dispose();throw error;}
        // KILL_ON_JOB_CLOSE | JOB_MEMORY: descendants share the same hard commit cap.
        var limits=new Limits { Basic=new Basic { Flags=0x2000|0x200 }, JobMemory=(UIntPtr)(ulong)cap };
        if(!SetInformationJobObject(_handle,9,ref limits,(uint)Marshal.SizeOf<Limits>())) { var error=new Win32Exception();Dispose();throw error; }
    }
    internal void Assign(Process process) { if(!AssignProcessToJobObject(_handle,process.Handle))throw new Win32Exception(); }
    internal long PeakBytes => QueryInformationJobObject(_handle,9,out var limits,(uint)Marshal.SizeOf<Limits>(),IntPtr.Zero) ? checked((long)limits.PeakJobMemory.ToUInt64()) : 0;
    internal void Kill() { if(_handle!=IntPtr.Zero)TerminateJobObject(_handle,77); }
    public void Dispose() { if(_handle!=IntPtr.Zero){CloseHandle(_handle);_handle=IntPtr.Zero;} if(_port!=IntPtr.Zero){CloseHandle(_port);_port=IntPtr.Zero;} }
}
