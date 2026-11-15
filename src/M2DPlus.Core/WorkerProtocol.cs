using System.Buffers.Binary;

namespace M2DPlus.Core;

/// <summary>Exact compatibility evidence exchanged before any scene payload.</summary>
public sealed record WorkerFingerprint(int Schema, int RuntimeMajor, string Architecture,
    string RhinoBuild, string RhinoCommonSha256, string EngineSha256, string RuntimeVersion = "", string RhinoRevision = "")
{
    public bool Accepts(WorkerFingerprint worker) => Schema > 0 &&
        RuntimeMajor is 8 or 10 && !string.IsNullOrWhiteSpace(RuntimeVersion) && !string.IsNullOrWhiteSpace(RhinoRevision) && !string.IsNullOrWhiteSpace(Architecture) &&
        !string.IsNullOrWhiteSpace(RhinoBuild) && IsHash(RhinoCommonSha256) &&
        IsHash(EngineSha256) && this == worker;

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Bounded framing for a duplex named-pipe stream. One reader and writer per direction.</summary>
public static class WorkerFrames
{
    public const int MaximumPayloadBytes = 1024 * 1024 * 1024;

    public static async Task WriteAsync(Stream pipe, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > MaximumPayloadBytes) throw new InvalidDataException("Worker frame exceeds limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumPayloadBytes) throw new InvalidDataException("Invalid worker frame length.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }
}

public sealed record WorkerPacket(string Kind, byte[] Payload);

/// <summary>Gzip envelope with a small operation header and raw bytes, avoiding a giant base64 JSON token.</summary>
public static class WorkerPackets
{
    public static byte[] Encode(string kind, ReadOnlySpan<byte> payload)
    {
        ValidateKind(kind);
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            zip.WriteByte((byte)kind.Length);
            zip.Write(System.Text.Encoding.ASCII.GetBytes(kind));
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            zip.Write(length);
            zip.Write(payload);
        }
        return output.ToArray();
    }
    public static WorkerPacket Decode(byte[] bytes, long expandedLimit = WorkerCompression.MaximumExpandedBytes)
    {
        if (expandedLimit <= 0) throw new ArgumentOutOfRangeException(nameof(expandedLimit));
        using var input = new MemoryStream(bytes, false);
        using var zip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        var kindLength = zip.ReadByte();
        if (kindLength is < 1 or > 16 || kindLength + 5L > expandedLimit) throw new InvalidDataException("Invalid worker operation header.");
        Span<byte> kindBytes = stackalloc byte[kindLength];
        zip.ReadExactly(kindBytes);
        var kind = System.Text.Encoding.ASCII.GetString(kindBytes);
        ValidateKind(kind);
        Span<byte> length = stackalloc byte[4];
        zip.ReadExactly(length);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count < 0 || count > Array.MaxLength || count > expandedLimit - kindLength - 5L)
            throw new InvalidDataException("Expanded worker envelope exceeds limit.");
        var payload = GC.AllocateUninitializedArray<byte>(count);
        zip.ReadExactly(payload);
        if (zip.ReadByte() != -1) throw new InvalidDataException("Trailing expanded worker envelope data.");
        return new(kind, payload);
    }
    private static void ValidateKind(string kind)
    {
        if (kind.Length is < 1 or > 16 || kind.Any(c => c is < 'a' or > 'z'))
            throw new InvalidDataException("Invalid worker operation.");
    }
}

/// <summary>Compressed protocol envelopes retain the frame bound and impose a separate expansion bound.</summary>
public static class WorkerCompression
{
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public static byte[] Encode<T>(T value, System.Text.Json.JsonSerializerOptions? options = null)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
            System.Text.Json.JsonSerializer.Serialize(zip, value, options);
        return output.ToArray();
    }
    public static T Decode<T>(byte[] bytes, System.Text.Json.JsonSerializerOptions? options = null, long expandedLimit = MaximumExpandedBytes)
    {
        if (expandedLimit <= 0) throw new ArgumentOutOfRangeException(nameof(expandedLimit));
        using var input = new MemoryStream(bytes, false);
        using var zip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var bounded = new ExpansionBound(zip, expandedLimit);
        return System.Text.Json.JsonSerializer.Deserialize<T>(bounded, options) ?? throw new InvalidDataException("Empty compressed worker envelope.");
    }
    private sealed class ExpansionBound(Stream source, long limit) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = source.Read(buffer[..(int)Math.Min(buffer.Length, limit - _read + 1)]);
            _read += count;
            if (_read > limit) throw new InvalidDataException("Expanded worker envelope exceeds limit.");
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
