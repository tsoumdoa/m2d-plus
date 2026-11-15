using System.Diagnostics;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;

internal static class EnvelopeBench
{
    internal static void Run(string folder)
    {
        var payload=File.ReadAllBytes(Path.Combine(folder,"scene.json"));
        var expected=WorkerCodec.Hash(payload);var rows=new List<object>();
        foreach(var raw in new[]{false,true})
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            var clock=Stopwatch.StartNew();
            try
            {
                var encoded=raw?WorkerPackets.Encode("scene",payload):WorkerCompression.Encode(new WorkerPacket("scene",payload));
                var encodeSeconds=clock.Elapsed.TotalSeconds;clock.Restart();
                var decoded=raw?WorkerPackets.Decode(encoded):WorkerCompression.Decode<WorkerPacket>(encoded);
                var decodeSeconds=clock.Elapsed.TotalSeconds;
                var hash=WorkerCodec.Hash(decoded.Payload);
                if(hash!=expected || decoded.Kind!="scene")throw new InvalidDataException("Envelope round-trip mismatch.");
                rows.Add(new{raw,encodeSeconds,decodeSeconds,compressedBytes=encoded.Length,hash,match=true});
            }
            catch(Exception ex){rows.Add(new{raw,seconds=clock.Elapsed.TotalSeconds,error=ex.ToString()});}
        }
        File.WriteAllBytes(Path.Combine(folder,"envelope-bench.json"),System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rows));
    }
}
