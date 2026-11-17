using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Xunit;

namespace M2DPlus.Rhino.Tests;

// No RhinoHost collection: these exercise OS process/job/pipe ownership without a licence.
public sealed class WorkerStartupCleanupTests
{
    private static readonly WorkerFingerprint Fingerprint = new(3,8,"X64","unused","unused","unused");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignmentFailureReapsBootstrapReleasesHandlesAndPreservesFailure(bool assigned)
    {
        var name="m2d-startup-test-"+Guid.NewGuid().ToString("N");
        Process? child=null;Process? owner=null;WorkerJob? job=null;Exception? assignmentFailure=null;
        try
        {
            var error=await Record.ExceptionAsync(()=>WorkerClient.StartAsync(Helper(),name,512L*1024*1024,1,
                TimeSpan.FromSeconds(5),Fingerprint,default,(ownedJob,process)=>
                {
                    job=ownedJob;owner=process;child=Process.GetProcessById(process.Id);
                    if(assigned)
                    {
                        ownedJob.Assign(process);
                        assignmentFailure=new IOException("Injected failure after assignment.");
                        throw assignmentFailure;
                    }
                    // A closed job deterministically makes the real Win32 assignment fail.
                    ownedJob.Dispose();
                    try{ownedJob.Assign(process);}catch(Exception ex){assignmentFailure=ex;throw;}
                }));
            Assert.NotNull(child);
            Assert.True(child.HasExited,"StartAsync returned while its uncontained bootstrap was still alive.");
            var unavailable=Assert.IsType<WorkerUnavailableException>(error);
            if(!assigned)Assert.IsType<Win32Exception>(assignmentFailure);
            Assert.Same(assignmentFailure,unavailable.InnerException);
            Assert.NotNull(owner);Assert.Throws<InvalidOperationException>(()=>owner.Handle);
            AssertJobClosed(job!);
            using var replacement=new NamedPipeServerStream(name,PipeDirection.InOut,1);
        }
        finally
        {
            // Only the helper started by this test is ever terminated, including on regression failure.
            if(child is not null){if(!child.HasExited){child.Kill();child.WaitForExit(10000);}child.Dispose();}
            owner?.Dispose();job?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingHandshakeTimeoutOrCancellationReapsOnlyItsChildAndAllowsRepeatedStartup(bool cancel)
    {
        var name="m2d-startup-test-"+Guid.NewGuid().ToString("N");
        using var unrelated=Process.Start(Helper())!;
        try
        {
            // Reuse the pipe name and repeat startup to detect unreleased server/process/job handles.
            for(var attempt=0;attempt<3;attempt++)
            {
                using var cancellation=new CancellationTokenSource();
                Process? child=null;Process? owner=null;WorkerJob? job=null;
                try
                {
                    var error=await Record.ExceptionAsync(()=>WorkerClient.StartAsync(Helper(),name,512L*1024*1024,1,
                        TimeSpan.FromMilliseconds(50),Fingerprint,cancellation.Token,(ownedJob,process)=>
                        {
                            job=ownedJob;owner=process;child=Process.GetProcessById(process.Id);
                            ownedJob.Assign(process);
                            if(cancel)cancellation.Cancel();
                        }));
                    Assert.NotNull(child);Assert.True(child.HasExited);
                    Assert.False(unrelated.HasExited,"Cleanup terminated a different owned helper.");
                    if(cancel)Assert.Equal(cancellation.Token,Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                    else Assert.IsAssignableFrom<OperationCanceledException>(Assert.IsType<WorkerUnavailableException>(error).InnerException);
                    Assert.NotNull(owner);Assert.Throws<InvalidOperationException>(()=>owner.Handle);
                    AssertJobClosed(job!);
                    using var replacement=new NamedPipeServerStream(name,PipeDirection.InOut,1);
                }
                finally
                {
                    if(child is not null){if(!child.HasExited){child.Kill();child.WaitForExit(10000);}child.Dispose();}
                    owner?.Dispose();job?.Dispose();
                }
            }
        }
        finally{if(!unrelated.HasExited){unrelated.Kill();unrelated.WaitForExit(10000);}}
    }

    [Fact]
    public async Task CleanupFailureRetainsBothErrorsAndStillReleasesPipeAndJob()
    {
        var name="m2d-startup-test-"+Guid.NewGuid().ToString("N");
        var startupFailure=new IOException("Injected startup failure.");
        Process? child=null;WorkerJob? job=null;
        try
        {
            var error=await Assert.ThrowsAsync<IOException>(()=>WorkerClient.StartAsync(Helper(),name,512L*1024*1024,1,
                TimeSpan.FromSeconds(5),Fingerprint,default,(ownedJob,process)=>
                {
                    job=ownedJob;child=Process.GetProcessById(process.Id);ownedJob.Assign(process);
                    // Simulate an unusable process wrapper during cleanup. The owned job still kills the helper.
                    process.Dispose();throw startupFailure;
                }));
            var failures=Assert.IsType<AggregateException>(error.InnerException);
            Assert.Equal(2,failures.InnerExceptions.Count);
            Assert.Same(startupFailure,failures.InnerExceptions[0]);
            Assert.IsType<InvalidOperationException>(failures.InnerExceptions[1]);
            Assert.NotNull(child);Assert.True(child.WaitForExit(10000));
            AssertJobClosed(job!);
            using var replacement=new NamedPipeServerStream(name,PipeDirection.InOut,1);
        }
        finally
        {
            if(child is not null){if(!child.HasExited){child.Kill();child.WaitForExit(10000);}child.Dispose();}
            job?.Dispose();
        }
    }

    [Fact]
    public async Task ProcessStartFailureReleasesPipeAndRetainsWin32Failure()
    {
        var name="m2d-startup-test-"+Guid.NewGuid().ToString("N");
        var start=new ProcessStartInfo(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"missing.exe")){UseShellExecute=false};
        var error=await Assert.ThrowsAsync<WorkerUnavailableException>(()=>WorkerClient.StartAsync(start,name,512L*1024*1024,1,
            TimeSpan.FromSeconds(5),Fingerprint,default));
        Assert.IsType<Win32Exception>(error.InnerException);
        using var replacement=new NamedPipeServerStream(name,PipeDirection.InOut,1);
    }

    private static ProcessStartInfo Helper()
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        // Stay alive even after stdin closes, so closing a pipe cannot hide a missing kill/reap.
        start.ArgumentList.Add("[Console]::ReadLine() | Out-Null; Start-Sleep -Seconds 120");
        return start;
    }

    private static void AssertJobClosed(WorkerJob job)
    {
        foreach(var name in new[]{"_handle","_port"})
            Assert.Equal(IntPtr.Zero,typeof(WorkerJob).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(job));
    }
}
