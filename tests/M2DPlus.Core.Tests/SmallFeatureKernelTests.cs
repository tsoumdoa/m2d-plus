using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class SmallFeatureKernelTests
{
    [Theory]
    [InlineData(.01)] [InlineData(.001)]
    public void AnalyticGapsAndShortEdgesSurviveUnitsAndOneKilometre(double tolerance)
    {
        foreach(var factor in new[]{1d,.001})
        foreach(var offset in new[]{0d,1000000})
        {
            Vertex P(double x,double y,double z) => new((offset+x)*factor,(offset+y)*factor,(offset+z)*factor);
            PlanarFace Rect(double a,double b) => new([[P(a,0,0),P(b,0,0),P(b,1,0),P(a,1,0)]]);
            var edges=new[]{new CandidateEdge(P(-.5,.5,-1),P(3.5,.5,-1),"probe"),
                new(P(0,2,1),P(.02,2,1),"short"),new(P(.03,2,1),P(.05,2,1),"short")};
            var result=EdgeFaceKernel.Solve([Rect(0,1),Rect(1.01,2.01)],edges,new(tolerance*factor));
            Assert.Empty(result.Diagnostics);
            var probe=result.Intervals.Where(i => i.Edge==0).ToArray();
            Assert.Equal(new[]{VisibilityClass.Visible,VisibilityClass.Hidden,VisibilityClass.Visible,VisibilityClass.Hidden,VisibilityClass.Visible},probe.Select(i => i.Class));
            var expected=new[]{(0d,.125),(.125,.375),(.375,.3775),(.3775,.6275),(.6275,1d)};
            for(var i=0;i<probe.Length;i++)
            {
                Assert.InRange(Math.Abs(probe[i].Start-expected[i].Item1),0,1e-8);
                Assert.InRange(Math.Abs(probe[i].End-expected[i].Item2),0,1e-8);
            }
            var loci=VisibilityLoci.Create(edges,result,tolerance*factor).Where(l => l.Source=="short").OrderBy(l => l.X0).ToArray();
            Assert.Equal(2,loci.Length);
            Assert.All(loci,l => Assert.InRange(Math.Abs(l.Length/factor-.02),0,1e-7));
            Assert.InRange(Math.Abs((loci[1].X0-loci[0].X1)/factor-.01),0,1e-7);
        }
    }
}
