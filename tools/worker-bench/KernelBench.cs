using System.Diagnostics;
using System.Text.Json;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Services;

internal static class KernelBench
{
    internal static void Run(string output)
    {
        var rows=new List<object>();
        foreach(var triangles in new[]{true,false})
        {
            var faces=Enumerable.Range(0,100_000).Select(i=>
            {
                double x=i%317,y=i/317,z=i%7*.125;
                Vertex[] vertices=triangles?[new(x,y,z),new(x+.75,y,z+.125),new(x,y+.75,z+.25)]:
                    [new(x,y,z),new(x+.75,y,z+.125),new(x+.75,y+.75,z+.375),new(x,y+.75,z+.25)];
                return new PlanarFace([vertices]);
            }).ToArray();
            var edges=Enumerable.Range(0,100).Select(i=>new CandidateEdge(new(i+.25,0,-1),new(i+.25,316,-1),i.ToString())).ToArray();
            for(var pass=0;pass<3;pass++)
            {
                GC.Collect();GC.WaitForPendingFinalizers();
                var watch=Stopwatch.StartNew();var result=EdgeFaceKernel.Solve(faces,edges,new(1e-6,1));
                var seconds=watch.Elapsed.TotalSeconds;
                var hash=WorkerCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(result));
                rows.Add(new{triangles,pass,seconds,hash,result.FaceTests,result.ExactFallbacks,result.Diagnostics});
                Console.WriteLine($"triangles={triangles} pass={pass}: {seconds:F3}s {hash}");
            }
        }
        File.WriteAllBytes(output,JsonSerializer.SerializeToUtf8Bytes(rows));
    }
}
