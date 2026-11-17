using System.Diagnostics;
using M2DPlus.Core.Visibility;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Core.Tests;

public sealed class UnitScaleTests(ITestOutputHelper output)
{
    private static (PlanarFace[] Faces, CandidateEdge[] Edges) Scene(int count, double scale, double offset)
    {
        Vertex V(double x,double y,double z) => new(x*scale+offset,y*scale+offset,z*scale+offset);
        var faces = Enumerable.Range(0,count).Select(i => {
            double x=i%80*2,y=i/80*2,z=i%7*.25;
            return new PlanarFace([[V(x,y,z),V(x+1.5,y,z+.25),V(x+1.5,y+1.5,z+.5),V(x,y+1.5,z+.25)]]);
        }).ToArray();
        var edges = Enumerable.Range(0,160).Select(i => new CandidateEdge(V(i*.75+.125,-1,-1),V(i*.75+.125,100,-1),i.ToString())).ToArray();
        return (faces,edges);
    }

    [Theory]
    [InlineData(1000,0)]
    [InlineData(1000,1000000)]
    [InlineData(1000,10000000)]
    public void MetresMillimetresAndSurveyOffsetsKeepTopologyAndClasses(double scale,double offset)
    {
        var a=Scene(160,1,0); var b=Scene(160,scale,offset);
        var expected=EdgeFaceKernel.Solve(a.Faces,a.Edges,new(.00001));
        var actual=EdgeFaceKernel.Solve(b.Faces,b.Edges,new(.00001*scale));
        Assert.Empty(actual.Diagnostics); Assert.Equal(expected.FaceTests,actual.FaceTests);
        Assert.Equal(expected.Intervals.Count,actual.Intervals.Count);
        for(int i=0;i<expected.Intervals.Count;i++)
        {
            var x=expected.Intervals[i];var y=actual.Intervals[i];
            Assert.Equal(x.Edge,y.Edge);Assert.Equal(x.Class,y.Class);
            Assert.Equal(x.Start,y.Start,10);Assert.Equal(x.End,y.End,10);
        }
        // Keep the independent, unmodified rational oracle as the correctness gate.
        var oracle=ExactVisibilityOracle.Solve(b.Faces,b.Edges.Take(4).ToArray(),.00001*scale);
        var production=EdgeFaceKernel.Solve(b.Faces,b.Edges.Take(4).ToArray(),new(.00001*scale));
        Assert.Equal(oracle.Intervals.Count,production.Intervals.Count);
        for(int i=0;i<oracle.Intervals.Count;i++)
        {
            Assert.Equal(oracle.Intervals[i].Edge,production.Intervals[i].Edge);
            Assert.Equal(oracle.Intervals[i].Class,production.Intervals[i].Class);
            Assert.Equal(oracle.Intervals[i].Start,production.Intervals[i].Start,12);
            Assert.Equal(oracle.Intervals[i].End,production.Intervals[i].End,12);
        }
    }

    [PerformanceFact, Trait("Category", "Performance")]
    public void MillimetresDoNotMultiplyPredicateWorkOrRuntime()
    {
        var m=Scene(6400,1,0);var mm=Scene(6400,1000,10000000);
        double Measure((PlanarFace[] Faces,CandidateEdge[] Edges) scene,double tolerance)
        {
            var clock=Stopwatch.StartNew();var result=EdgeFaceKernel.Solve(scene.Faces,scene.Edges,new(tolerance));
            Assert.Empty(result.Diagnostics);return clock.Elapsed.TotalSeconds;
        }
        Measure(m,.00001);Measure(mm,.01);
        var a=new double[5];var b=new double[5];
        for(int i=0;i<5;i++)
            if(i%2==0){a[i]=Measure(m,.00001);b[i]=Measure(mm,.01);}
            else{b[i]=Measure(mm,.01);a[i]=Measure(m,.00001);}
        Array.Sort(a);Array.Sort(b);output.WriteLine($"Median m={a[2]:F4}s mm+survey={b[2]:F4}s ratio={b[2]/a[2]:F3}");
        Assert.True(b[2]<=a[2]*1.5+.02,$"mm+survey={b[2]:F4}s exceeds 1.5 x m={a[2]:F4}s (20ms timer reserve)");
    }

    [Fact]
    public void ExhaustedCertificateBudgetWithholdsTheWholeCandidateAndReportsIt()
    {
        PlanarFace face=new([[new(0,0,1),new(2,0,1),new(2,2,1),new(0,2,1)]]);
        CandidateEdge edge=new(new(-1,1,0),new(3,1,0),"cross");
        var result=EdgeFaceKernel.Solve([face],[edge],new(.001){MaxPairOperations=4});
        Assert.Equal(VisibilityClass.Unresolved,Assert.Single(result.Intervals).Class);
        Assert.Contains(result.Diagnostics,d=>d.Contains("budget exceeded",StringComparison.Ordinal));
        var many=Enumerable.Repeat(face,5).ToArray();
        var limited=EdgeFaceKernel.Solve(many,[edge],new(.001){MaxFacesPerEdge=1});
        // No cap can erase an already complete hidden proof; a partial proof cannot imply visibility.
        Assert.DoesNotContain(limited.Intervals,i=>i.Class==VisibilityClass.Visible);
    }

    [Fact]
    public void InnerCertificateWorkChecksCancellation()
    {
        var scene=Scene(6400,1000,10000000);using var cancel=new CancellationTokenSource();
        int events=0;
        Assert.ThrowsAny<OperationCanceledException>(()=>EdgeFaceKernel.Solve(scene.Faces,scene.Edges,new(.01)
        { Progress=(_,_)=>{if(Interlocked.Increment(ref events)==2)cancel.Cancel();} },cancel.Token));
    }
}
