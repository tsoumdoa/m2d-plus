using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Xunit;
namespace M2DPlus.Rhino.Tests;
[Collection("Rhino")]
public sealed class WorkerNativeSettingsTests(RhinoHost host)
{
    [RhinoFact] public void RequestCarriesOnlySupportedPolicyAndUsesWorkerScheduler() => host.Run(() =>
    {
        Assert.Empty(typeof(NativeSettings).GetProperties());
        var sent=WorkerCodec.Request.FromSettings(new("s","v","f",7),2,EngineNativeTests.View("hidden=off"),new(){CullSources=false});
        var received=WorkerCodec.Decode<WorkerCodec.Request>(WorkerCodec.Encode(sent));
        var scheduler=new NativeScheduler(123456789);var restored=received.ToSettings(scheduler);
        Assert.False(restored.CullSources);Assert.True(restored.IsWorker);Assert.Same(scheduler,restored.Scheduler);
        Assert.Equal(sent.Identity,received.Identity);Assert.Equal(2,received.ViewIndex);
        var flags=NativeHldOptions.For(received.View,restored);Assert.True(flags.MultipleThreads);Assert.False(flags.IncludeHiddenCurves);
        Assert.Equal(4,WorkerCodec.Schema);
        Assert.Equal(new[]{"CullSources","Identity","View","ViewIndex"},typeof(WorkerCodec.Request).GetProperties().Select(p=>p.Name).Order(StringComparer.Ordinal));
    });
}
