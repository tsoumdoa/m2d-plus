using System.Buffers.Binary;
using M2DPlus.Core;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class WorkerProtocolTests
{
    private static WorkerFingerprint Identity => new(1,8,"X64","8.36.26296.17001",new('a',64),new('b',64),"8.0.34","revision");
    [Fact] public void ExactIdentityRequired()
    {
        var f=Identity;Assert.True(f.Accepts(f));
        foreach(var changed in new[]{f with {Schema=2},f with{RuntimeMajor=10},f with{RuntimeVersion="8.0.35"},f with{Architecture="Arm64"},
            f with{RhinoBuild="8.37"},f with{RhinoRevision="different"},f with{RhinoCommonSha256=new('c',64)},f with{EngineSha256=new('c',64)}})
            Assert.False(f.Accepts(changed));
        Assert.False((f with{Schema=0}).Accepts(f with{Schema=0}));
    }
    [Fact] public async Task FramingRoundTripAndTruncation()
    {
        using var stream=new MemoryStream();await WorkerFrames.WriteAsync(stream,new byte[]{1,2,3},default);stream.Position=0;
        Assert.Equal(new byte[]{1,2,3},await WorkerFrames.ReadAsync(stream,default));
        stream.SetLength(6);stream.Position=0;await Assert.ThrowsAsync<EndOfStreamException>(()=>WorkerFrames.ReadAsync(stream,default));
    }
    [Theory] [InlineData(-1)] [InlineData(int.MaxValue)] public async Task BadLengthsRefusedBeforeAllocation(int length)
    {
        var bytes=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(bytes,length);using var stream=new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerFrames.ReadAsync(stream,default));
    }
    [Fact] public async Task CancelledReadStops()
    {
        using var stream=new MemoryStream();using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WorkerFrames.ReadAsync(stream,cancel.Token));
    }
    [Fact] public void CompressedEnvelopeRoundTripsAndRejectsExpansionBomb()
    {
        var value=new string('a',1024*1024);var packed=WorkerCompression.Encode(value);
        Assert.True(packed.Length<16384);Assert.Equal(value,WorkerCompression.Decode<string>(packed));
        Assert.Throws<InvalidDataException>(()=>WorkerCompression.Decode<string>(packed,expandedLimit:4096));
        Assert.Throws<InvalidDataException>(()=>WorkerCompression.Decode<string>(new byte[]{1,2,3,4}));
    }

}
