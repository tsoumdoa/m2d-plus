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
    [Fact] public void RawPacketPreservesBytesAndRejectsExpansionBeforeAllocation()
    {
        var value=new byte[1024*1024];new Random(17).NextBytes(value);
        var packed=WorkerPackets.Encode("scene",value);var restored=WorkerPackets.Decode(packed);
        Assert.Equal("scene",restored.Kind);Assert.Equal(value,restored.Payload);
        Assert.Empty(WorkerPackets.Decode(WorkerPackets.Encode("started",[])).Payload);
        Assert.Throws<InvalidDataException>(()=>WorkerPackets.Decode(packed,4096));
        Assert.Throws<InvalidDataException>(()=>WorkerPackets.Decode([1,2,3,4]));
    }
    [Theory] [InlineData("")] [InlineData("abcdefghijklmnopq")] [InlineData("SCENE")] [InlineData("sλ")]
    public void RawPacketOperationHeaderIsBounded(string kind) => Assert.Throws<InvalidDataException>(()=>WorkerPackets.Encode(kind,[]));
    [Theory] [InlineData(-1)] [InlineData(int.MaxValue)] [InlineData(100)]
    public void RawPacketBadPayloadLengthIsRefusedBeforeAllocation(int count)
    {
        var raw=new byte[10];raw[0]=5;"scene"u8.CopyTo(raw.AsSpan(1));BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(6),count);
        Assert.Throws<InvalidDataException>(()=>WorkerPackets.Decode(Zip(raw),64));
    }
    [Fact] public void RawPacketRejectsTruncationAndTrailingExpandedBytes()
    {
        var raw=new byte[12];raw[0]=5;"scene"u8.CopyTo(raw.AsSpan(1));BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(6),3);
        Assert.Throws<EndOfStreamException>(()=>WorkerPackets.Decode(Zip(raw)));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(6),1);
        Assert.Throws<InvalidDataException>(()=>WorkerPackets.Decode(Zip(raw)));
        raw[0]=17;Assert.Throws<InvalidDataException>(()=>WorkerPackets.Decode(Zip(raw)));
    }
    private static byte[] Zip(byte[] raw)
    {
        using var output=new MemoryStream();
        using(var zip=new System.IO.Compression.GZipStream(output,System.IO.Compression.CompressionLevel.Fastest,true))zip.Write(raw);
        return output.ToArray();
    }

    [Fact] public async Task StreamingRepliesPreserveFramesAcrossShortReads()
    {
        var value=new byte[1024*1024];new Random(29).NextBytes(value);
        using var frames=new ShortReads();
        await WorkerFrames.WriteAsync(frames,WorkerPackets.Encode("solve",value),default);
        await WorkerFrames.WriteAsync(frames,WorkerPackets.Encode("started",[]),default);
        frames.Position=0;
        var reply=await WorkerPackets.ReadAsync(frames,value.Length+32L,default);
        Assert.Equal("solve",reply.Kind);Assert.Equal(value,reply.Payload);
        Assert.Equal("started",(await WorkerPackets.ReadAsync(frames,32,default)).Kind);
        Assert.Equal(frames.Length,frames.Position);Assert.True(frames.CanRead);
    }
    [Fact] public async Task StreamingReplyRejectsDeclaredExpansionBeforeReadingPayload()
    {
        var raw=new byte[10];raw[0]=5;"solve"u8.CopyTo(raw.AsSpan(1));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(6),Array.MaxLength);
        using var frames=new MemoryStream();await WorkerFrames.WriteAsync(frames,Zip(raw),default);frames.Position=0;
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerPackets.ReadAsync(frames,4096,default));
        Assert.Contains("host receive reservation",error.Message);
    }
    [Fact] public async Task StreamingReplyChecksTruncationTrailingDataAndCancellation()
    {
        var raw=new byte[12];raw[0]=5;"solve"u8.CopyTo(raw.AsSpan(1));
        foreach(var count in new[]{3,1})
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(6),count);
            using var frames=new MemoryStream();await WorkerFrames.WriteAsync(frames,Zip(raw),default);frames.Position=0;
            if(count==3)await Assert.ThrowsAsync<EndOfStreamException>(()=>WorkerPackets.ReadAsync(frames,64,default));
            else await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerPackets.ReadAsync(frames,64,default));
        }
        using var headerOnly=new MemoryStream(new byte[4]);
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,WorkerFrames.MaximumPayloadBytes);
        using var absentPayload=new MemoryStream(header);
        await Assert.ThrowsAsync<EndOfStreamException>(()=>WorkerPackets.ReadAsync(absentPayload,64,default));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WorkerPackets.ReadAsync(headerOnly,64,cancel.Token));
    }
    private sealed class ShortReads : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) =>
            base.ReadAsync(buffer[..Math.Min(137,buffer.Length)],token);
    }

}
